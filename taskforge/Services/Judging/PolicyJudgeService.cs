using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using System.Text.Json;
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
    private readonly ApplicationDbContext _db;
    private readonly ICodeAnalyzerClient _analyzer;
    private readonly CodeAnalyzerOptions _opt;

    public PolicyJudgeService(
        IJudgeService inner,
        ICodeAnalyzerClient analyzer,
        Microsoft.Extensions.Options.IOptions<CodeAnalyzerOptions> opt,
        ApplicationDbContext db)
    {
        _inner = inner;
        _db = db;
        _analyzer = analyzer;
        _opt = opt.Value;
    }

    

private static System.Collections.Generic.List<string> ParseList(JsonDocument? json)
    {
        if (json is null) return new System.Collections.Generic.List<string>();
        try
        {
            if (json.RootElement.ValueKind != JsonValueKind.Array) return new System.Collections.Generic.List<string>();
            return json.RootElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new System.Collections.Generic.List<string>();
        }
    }
    catch
    {
        return new();
    }
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

        // Load per-task call rules from DB (optional)
        var task = await _db.TaskAssignments.AsNoTracking()
            .Where(x => x.Id == req.AssignmentId)
            .Select(x => new { x.CodeForbiddenCallsJson, x.CodeRequiredCallsJson })
            .FirstOrDefaultAsync();
        var forbCalls = ParseList(task?.CodeForbiddenCallsJson);
        var reqCalls = ParseList(task?.CodeRequiredCallsJson);
        Console.WriteLine($"[PolicyJudge] task policy: forbidden_calls={forbCalls.Count} required_calls={reqCalls.Count}");

        try
        {
            Console.WriteLine($"[PolicyJudge] analyzer -> calling /analyze (url='{_opt.Url}')");
            ares = await _analyzer.AnalyzeAsync(new AnalyzeRequest
            {
                Language = req.Language,
                Source = req.Source,
                ExtraForbidden = null,
                ForbiddenCalls = forbCalls.Count > 0 ? forbCalls : null,
                RequiredCalls = reqCalls.Count > 0 ? reqCalls : null
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
