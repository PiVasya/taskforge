using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO.Solutions;
using taskforge.Services.CodeAnalysis;
using taskforge.Services.CodeAnalysis.Models;
using taskforge.Services.Interfaces;

namespace taskforge.Services;

/// <summary>
/// New judging pipeline (v2):
/// 1) run code-analyzer (forbidden constructs)
/// 2) if ok -> delegate to existing JudgeService (I/O tests)
/// </summary>
public sealed class PolicyJudgeService : IPolicyJudgeService
{
    private readonly IJudgeService _inner;
    private readonly ICodeAnalyzerClient _analyzer;
    private readonly CodeAnalyzerOptions _opt;

    public PolicyJudgeService(
        IJudgeService inner,
        ICodeAnalyzerClient analyzer,
        Microsoft.Extensions.Options.IOptions<CodeAnalyzerOptions> opt)
    {
        _inner = inner;
        _analyzer = analyzer;
        _opt = opt.Value;
    }

    public async Task<JudgeResponseDto> JudgeAsync(JudgeRequestDto req, Guid currentUserId)
    {
        // If analyzer disabled or url not set -> fallback to old behavior
        if (!_opt.Enabled || string.IsNullOrWhiteSpace(_opt.Url))
            return await _inner.JudgeAsync(req, currentUserId);

        AnalyzeResponse? ares;
        try
        {
            ares = await _analyzer.AnalyzeAsync(new AnalyzeRequest
            {
                Language = req.Language,
                Source = req.Source,
                ExtraForbidden = null
            });
        }
        catch (Exception ex) when (ex is HttpRequestException)
        {
            // Infrastructure error: do not run user code if analyzer is mandatory
            return new JudgeResponseDto
            {
                Status = "infrastructure_error",
                Message = "Сервис анализа кода недоступен",
                Compile = new JudgeResponseDto.CompileInfo
                {
                    Ok = false,
                    Stdout = "",
                    Stderr = ex.Message,
                    Diagnostics = new()
                },
                Run = new JudgeResponseDto.RunInfo { ExitCode = -1, Stdout = "", Stderr = ex.Message }
            };
        }

        if (ares != null && !ares.Ok)
        {
            var sb = new StringBuilder();
            foreach (var e in ares.Errors.Take(10))
            {
                sb.Append("• ").Append(e.Message);
                if (!string.IsNullOrWhiteSpace(e.PatternId)) sb.Append($" ({e.PatternId})");
                sb.AppendLine();
            }

            return new JudgeResponseDto
            {
                Status = "policy_failed",
                Message = "Код содержит запрещённые конструкции",
                Compile = new JudgeResponseDto.CompileInfo
                {
                    Ok = true,
                    Stdout = "",
                    Stderr = sb.ToString().TrimEnd(),
                    Diagnostics = new()
                },
                Run = new JudgeResponseDto.RunInfo { ExitCode = 0, Stdout = "", Stderr = "" }
            };
        }

        return await _inner.JudgeAsync(req, currentUserId);
    }
}
