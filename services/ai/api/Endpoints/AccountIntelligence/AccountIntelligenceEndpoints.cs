using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;
using TaskForge.Ai.Api.Services.AccountIntelligence;

namespace TaskForge.Ai.Api.Endpoints;

internal static partial class AiApiEndpoints
{
    private sealed record AccountDecisionRequest(string? Decision, string? Note);

    private static WebApplication MapAccountIntelligenceEndpoints(WebApplication app)
    {
        app.MapPost("/api/admin/ai/account-manager/runs", async (
            HttpContext http,
            IConfiguration cfg,
            AiDbContext db,
            CancellationToken ct) =>
        {
            var requestedBy = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!requestedBy.HasValue) return Results.Unauthorized();

            var active = await db.AccountAnalysisRuns.AsNoTracking()
                .Where(x => x.Status == "queued" || x.Status == "starting" || x.Status == "running")
                .OrderByDescending(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (active != null)
            {
                return Results.Conflict(new
                {
                    message = "Анализ аккаунтов уже выполняется.",
                    code = "ACCOUNT_ANALYSIS_ALREADY_RUNNING",
                    run = ToAccountAnalysisRunDto(active),
                });
            }

            var run = new AccountAnalysisRun
            {
                RequestedByUserId = requestedBy.Value,
                Status = "queued",
                Phase = "queued",
                ProgressPercent = 0,
                AlgorithmVersion = "account-intelligence-v1.1",
            };
            db.AccountAnalysisRuns.Add(run);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/admin/ai/account-manager/runs/{run.Id}", ToAccountAnalysisRunDto(run));
        });

        app.MapGet("/api/admin/ai/account-manager/runs/latest", async (AiDbContext db, CancellationToken ct) =>
        {
            var run = await db.AccountAnalysisRuns.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
            return run == null ? Results.Ok(new { run = (object?)null }) : Results.Ok(new { run = ToAccountAnalysisRunDto(run) });
        });

        app.MapGet("/api/admin/ai/account-manager/runs", async (AiDbContext db, int take = 20, CancellationToken ct = default) =>
        {
            take = Math.Clamp(take, 1, 100);
            var rows = await db.AccountAnalysisRuns.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Take(take).ToListAsync(ct);
            return Results.Ok(rows.Select(ToAccountAnalysisRunDto).ToList());
        });

        app.MapGet("/api/admin/ai/account-manager/runs/{runId:guid}", async (Guid runId, AiDbContext db, CancellationToken ct) =>
        {
            var run = await db.AccountAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, ct);
            if (run == null) return Results.NotFound(new { message = "Запуск анализа не найден.", code = "ACCOUNT_ANALYSIS_RUN_NOT_FOUND" });
            return Results.Ok(ToAccountAnalysisRunDto(run));
        });

        app.MapGet("/api/admin/ai/account-manager/findings", async (
            AiDbContext db,
            Guid? runId,
            string? kind,
            string? status,
            string? q,
            int minScore = 0,
            int page = 1,
            int pageSize = 50,
            CancellationToken ct = default) =>
        {
            var selectedRunId = runId;
            if (!selectedRunId.HasValue)
            {
                selectedRunId = await db.AccountAnalysisRuns.AsNoTracking()
                    .Where(x => x.Status == "completed")
                    .OrderByDescending(x => x.CompletedAtUtc)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync(ct);
            }
            if (!selectedRunId.HasValue)
            {
                return Results.Ok(new { runId = (Guid?)null, total = 0, page = 1, pageSize, items = Array.Empty<object>() });
            }

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 10, 200);
            minScore = Math.Clamp(minScore, 0, 100);
            var query = db.AccountAnalysisFindings.AsNoTracking().Where(x => x.RunId == selectedRunId.Value && x.Score >= minScore);
            if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(x => x.Kind == kind.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.Trim().ToLowerInvariant());

            if (string.IsNullOrWhiteSpace(q))
            {
                var total = await query.CountAsync(ct);
                var pageRows = await query
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.CreatedAtUtc)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync(ct);
                return Results.Ok(new { runId = selectedRunId, total, page, pageSize, items = pageRows.Select(ToFindingDto).ToList() });
            }

            var search = q.Trim();
            var rows = await query.OrderByDescending(x => x.Score).ThenByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
            rows = rows.Where(x => x.DataJson.Contains(search, StringComparison.OrdinalIgnoreCase) || x.FindingKey.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
            var filteredTotal = rows.Count;
            var filteredItems = rows.Skip((page - 1) * pageSize).Take(pageSize).Select(ToFindingDto).ToList();
            return Results.Ok(new { runId = selectedRunId, total = filteredTotal, page, pageSize, items = filteredItems });
        });

        app.MapGet("/api/admin/ai/account-manager/findings/{findingId:guid}", async (Guid findingId, AiDbContext db, CancellationToken ct) =>
        {
            var finding = await db.AccountAnalysisFindings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == findingId, ct);
            return finding == null
                ? Results.NotFound(new { message = "Результат анализа не найден.", code = "ACCOUNT_FINDING_NOT_FOUND" })
                : Results.Ok(ToFindingDto(finding));
        });

        app.MapPost("/api/admin/ai/account-manager/findings/{findingId:guid}/decision", async (
            Guid findingId,
            AccountDecisionRequest request,
            HttpContext http,
            IConfiguration cfg,
            AiDbContext db,
            CancellationToken ct) =>
        {
            var reviewer = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!reviewer.HasValue) return Results.Unauthorized();
            var finding = await db.AccountAnalysisFindings.FirstOrDefaultAsync(x => x.Id == findingId, ct);
            if (finding == null) return Results.NotFound(new { message = "Результат анализа не найден.", code = "ACCOUNT_FINDING_NOT_FOUND" });

            var decision = NormalizePairDecision(request.Decision);
            if (decision == null) return Results.BadRequest(new { message = "Допустимые решения: duplicate, different, ignored.", code = "INVALID_ACCOUNT_DECISION" });
            var review = await db.AccountAnalysisReviews.FirstOrDefaultAsync(x => x.SubjectType == "pair" && x.SubjectKey == finding.FindingKey, ct);
            if (review == null)
            {
                review = new AccountAnalysisReview { SubjectType = "pair", SubjectKey = finding.FindingKey };
                db.AccountAnalysisReviews.Add(review);
            }
            review.UserId = finding.PrimaryUserId;
            review.OtherUserId = finding.SecondaryUserId;
            review.Decision = decision;
            review.Note = CleanNote(request.Note);
            review.SignalsJson = ExtractSignalCodes(finding.DataJson);
            review.ReviewedByUserId = reviewer.Value;
            review.UpdatedAtUtc = DateTimeOffset.UtcNow;
            finding.Status = decision switch { "duplicate" => "confirmed", "different" => "dismissed", _ => "ignored" };
            finding.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { review = ToReviewDto(review), finding = ToFindingDto(finding) });
        });

        app.MapPost("/api/admin/ai/account-manager/accounts/{userId:guid}/decision", async (
            Guid userId,
            AccountDecisionRequest request,
            HttpContext http,
            IConfiguration cfg,
            AiDbContext db,
            CancellationToken ct) =>
        {
            var reviewer = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!reviewer.HasValue) return Results.Unauthorized();
            var decision = NormalizeAccountDecision(request.Decision);
            if (decision == null) return Results.BadRequest(new { message = "Допустимые решения: verified, unverified, ignored.", code = "INVALID_ACCOUNT_DECISION" });
            var key = AccountSimilarityEngine.AccountKey(userId);
            var review = await db.AccountAnalysisReviews.FirstOrDefaultAsync(x => x.SubjectType == "account" && x.SubjectKey == key, ct);
            if (review == null)
            {
                review = new AccountAnalysisReview { SubjectType = "account", SubjectKey = key, UserId = userId };
                db.AccountAnalysisReviews.Add(review);
            }
            review.Decision = decision;
            review.Note = CleanNote(request.Note);
            review.ReviewedByUserId = reviewer.Value;
            review.UpdatedAtUtc = DateTimeOffset.UtcNow;

            var latestFindingJson = await db.AccountAnalysisFindings.AsNoTracking()
                .Where(x => x.PrimaryUserId == userId || x.SecondaryUserId == userId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Select(x => x.DataJson)
                .FirstOrDefaultAsync(ct);
            review.DataJson = ExtractAccountSnapshot(latestFindingJson, userId) ?? review.DataJson;

            var suspiciousRows = await db.AccountAnalysisFindings
                .Where(x => x.Kind == "suspicious" && x.PrimaryUserId == userId && x.Status == "open")
                .ToListAsync(ct);
            foreach (var row in suspiciousRows)
            {
                row.Status = decision == "verified" ? "verified" : decision == "ignored" ? "ignored" : "open";
                row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToReviewDto(review));
        });

        app.MapGet("/api/admin/ai/account-manager/reviews", async (AiDbContext db, string? subjectType, int take = 500, CancellationToken ct = default) =>
        {
            take = Math.Clamp(take, 1, 2000);
            var query = db.AccountAnalysisReviews.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(subjectType)) query = query.Where(x => x.SubjectType == subjectType.Trim().ToLowerInvariant());
            var rows = await query.OrderByDescending(x => x.UpdatedAtUtc).Take(take).ToListAsync(ct);
            return Results.Ok(rows.Select(ToReviewDto).ToList());
        });

        return app;
    }

    private static object ToAccountAnalysisRunDto(AccountAnalysisRun run) => new
    {
        run.Id,
        run.RequestedByUserId,
        run.Status,
        run.Phase,
        run.ProgressPercent,
        run.TotalAccounts,
        run.CandidatePairs,
        run.DuplicateFindings,
        run.SuspiciousFindings,
        run.AlgorithmVersion,
        sources = ParseAccountJson(run.SourcesJson),
        error = ParseAccountJson(run.ErrorJson),
        run.StartedAtUtc,
        run.CompletedAtUtc,
        run.CreatedAtUtc,
        run.UpdatedAtUtc,
    };

    private static object ToFindingDto(AccountAnalysisFinding finding) => new
    {
        finding.Id,
        finding.RunId,
        finding.FindingKey,
        finding.Kind,
        finding.Status,
        finding.Score,
        finding.ModelProbability,
        finding.PrimaryUserId,
        finding.SecondaryUserId,
        finding.SuggestedPrimaryUserId,
        data = ParseAccountJson(finding.DataJson),
        finding.CreatedAtUtc,
        finding.UpdatedAtUtc,
    };

    private static object ToReviewDto(AccountAnalysisReview review) => new
    {
        review.Id,
        review.SubjectType,
        review.SubjectKey,
        review.UserId,
        review.OtherUserId,
        review.Decision,
        review.Note,
        signals = ParseAccountJson(review.SignalsJson),
        data = ParseAccountJson(review.DataJson),
        review.ReviewedByUserId,
        review.CreatedAtUtc,
        review.UpdatedAtUtc,
    };

    private static string? ExtractAccountSnapshot(string? json, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("account", out var single) && MatchesUser(single, userId))
                return single.GetRawText();
            if (root.TryGetProperty("accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var account in accounts.EnumerateArray())
                {
                    if (MatchesUser(account, userId)) return account.GetRawText();
                }
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private static bool MatchesUser(JsonElement account, Guid userId)
    {
        if (!account.TryGetProperty("userId", out var value)) return false;
        return Guid.TryParse(value.ToString(), out var parsed) && parsed == userId;
    }

    private static JsonElement? ParseAccountJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return null; }
    }

    private static string? ExtractSignalCodes(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("signalCodes", out var signals) || signals.ValueKind != JsonValueKind.Array) return null;
            return signals.GetRawText();
        }
        catch { return null; }
    }

    private static string? NormalizePairDecision(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "duplicate" or "same" or "confirm" => "duplicate",
        "different" or "not-duplicate" => "different",
        "ignored" or "ignore" => "ignored",
        _ => null,
    };

    private static string? NormalizeAccountDecision(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "verified" or "checked" => "verified",
        "unverified" or "reset" => "unverified",
        "ignored" or "ignore" => "ignored",
        _ => null,
    };

    private static string? CleanNote(string? value)
    {
        var note = value?.Trim();
        if (string.IsNullOrWhiteSpace(note)) return null;
        return note.Length <= 1000 ? note : note[..1000];
    }
}
