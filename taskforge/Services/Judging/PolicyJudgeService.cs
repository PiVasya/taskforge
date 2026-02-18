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
        Console.WriteLine("[PolicyJudge] >>> start");
        Console.WriteLine($"[PolicyJudge] userId={currentUserId} lang='{req.Language}' source.len={req.Source?.Length ?? 0}");

        // If analyzer disabled or url not set -> fallback to old behavior
        if (!_opt.Enabled || string.IsNullOrWhiteSpace(_opt.Url))
        {
            Console.WriteLine($"[PolicyJudge] analyzer disabled (Enabled={_opt.Enabled}, Url='{_opt.Url ?? ""}') -> fallback to old JudgeService");
            return await _inner.JudgeAsync(req, currentUserId);
        }

        AnalyzeResponse? ares;
        try
        {
            Console.WriteLine($"[PolicyJudge] analyzer -> calling /analyze (url='{_opt.Url}')");
            ares = await _analyzer.AnalyzeAsync(new AnalyzeRequest
            {
                Language = req.Language,
                Source = req.Source,
                ExtraForbidden = null
            });
            Console.WriteLine($"[PolicyJudge] analyzer <- ok={ares?.Ok} errors={ares?.Errors?.Count ?? 0} hits={ares?.Hits?.Count ?? 0}");
        }
        catch (Exception ex) when (ex is HttpRequestException)
        {
            Console.WriteLine($"[PolicyJudge] analyzer EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex);
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
            Console.WriteLine("[PolicyJudge] POLICY_FAIL -> returning policy_failed (do not run code)");
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

        Console.WriteLine("[PolicyJudge] policy OK -> delegating to old JudgeService");
        return await _inner.JudgeAsync(req, currentUserId);
    }
}
