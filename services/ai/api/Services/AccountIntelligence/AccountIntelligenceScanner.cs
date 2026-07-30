using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;

namespace TaskForge.Ai.Api.Services.AccountIntelligence;

internal sealed class AccountIntelligenceScanner(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<AccountIntelligenceScanner> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public async Task ProcessRunAsync(Guid runId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        var run = await db.AccountAnalysisRuns.FirstOrDefaultAsync(x => x.Id == runId, ct);
        if (run == null) return;

        try
        {
            run.Status = "running";
            run.Phase = "collecting-data";
            run.ProgressPercent = 5;
            run.StartedAtUtc ??= DateTimeOffset.UtcNow;
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var reviews = await db.AccountAnalysisReviews.AsNoTracking().ToListAsync(ct);
            var reviewByKey = reviews.ToDictionary(x => x.SubjectKey, StringComparer.OrdinalIgnoreCase);
            var verifiedIds = reviews
                .Where(x => x.SubjectType == "account" && x.Decision == "verified" && x.UserId.HasValue)
                .Select(x => x.UserId!.Value)
                .ToHashSet();
            var learning = BuildLearningProfile(reviews);

            var bundle = await LoadSnapshotBundleAsync(verifiedIds, ct);
            run.TotalAccounts = bundle.Accounts.Count;
            run.SourcesJson = JsonSerializer.Serialize(new { bundle.Sources, bundle.Warnings }, JsonOptions);
            run.Phase = "building-candidates";
            run.ProgressPercent = 25;
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var accounts = bundle.Accounts
                .Where(x => !string.Equals(x.Identity.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Identity.CreatedAt)
                .ToList();
            var pairs = BuildCandidatePairs(accounts);
            AddConfirmedReviewPairs(pairs, accounts, reviews);
            run.CandidatePairs = pairs.Count;
            run.Phase = "matching-accounts";
            run.ProgressPercent = 35;
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var now = DateTimeOffset.UtcNow;
            var findings = new List<AccountAnalysisFinding>();
            var processed = 0;
            var progressEvery = Math.Max(500, pairs.Count / 100);
            foreach (var (a, b) in pairs)
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                var pairKey = AccountSimilarityEngine.PairKey(a.UserId, b.UserId);
                if (reviewByKey.TryGetValue(pairKey, out var prior) && prior.Decision is "different" or "ignored") continue;

                var analysis = AccountSimilarityEngine.AnalyzePair(a, b, learning, now);
                var confirmedDuplicate = prior?.Decision == "duplicate";
                if (!confirmedDuplicate && analysis.FinalScore < 42) continue;
                if (!confirmedDuplicate && a.Verified && b.Verified) continue;

                var older = a.Identity.CreatedAt <= b.Identity.CreatedAt ? a : b;
                var newer = older.UserId == a.UserId ? b : a;
                var aActivity = AccountSimilarityEngine.ActivityScore(a, now);
                var bActivity = AccountSimilarityEngine.ActivityScore(b, now);
                var currentlyActive = aActivity >= bActivity ? a : b;
                var aHistory = AccountSimilarityEngine.HistoricalValueScore(a);
                var bHistory = AccountSimilarityEngine.HistoricalValueScore(b);
                var historicallyRicher = aHistory >= bHistory ? a : b;
                var verifiedAnchor = a.Verified ^ b.Verified ? (a.Verified ? a.UserId : b.UserId) : (Guid?)null;
                var suspect = verifiedAnchor.HasValue ? (verifiedAnchor.Value == a.UserId ? b.UserId : a.UserId) : newer.UserId;
                var status = prior?.Decision == "duplicate" ? "confirmed" : "open";

                var data = new
                {
                    pairKey,
                    algorithmVersion = run.AlgorithmVersion,
                    accounts = new[] { AccountView(a, now), AccountView(b, now) },
                    olderUserId = older.UserId,
                    newerUserId = newer.UserId,
                    currentlyActiveUserId = currentlyActive.UserId,
                    historicallyRicherUserId = historicallyRicher.UserId,
                    suggestedPrimaryUserId = analysis.SuggestedPrimaryUserId,
                    verifiedAnchorUserId = verifiedAnchor,
                    suspectUserId = suspect,
                    registrationGapDays = Math.Round(Math.Abs((a.Identity.CreatedAt - b.Identity.CreatedAt).TotalDays), 1),
                    evidence = analysis.Evidence,
                    signalCodes = analysis.SignalCodes,
                    baseScore = analysis.BaseScore,
                    finalScore = analysis.FinalScore,
                    probability = analysis.Probability,
                    learning = new { labels = learning.LabelCount, active = learning.LabelCount >= 10 },
                };

                findings.Add(new AccountAnalysisFinding
                {
                    RunId = run.Id,
                    FindingKey = pairKey,
                    Kind = "duplicate",
                    Status = status,
                    Score = analysis.FinalScore,
                    ModelProbability = analysis.Probability,
                    PrimaryUserId = suspect,
                    SecondaryUserId = verifiedAnchor ?? (suspect == a.UserId ? b.UserId : a.UserId),
                    SuggestedPrimaryUserId = analysis.SuggestedPrimaryUserId,
                    DataJson = JsonSerializer.Serialize(data, JsonOptions),
                });

                if (processed % progressEvery == 0)
                {
                    run.ProgressPercent = 35 + Math.Min(40, (int)Math.Round(40.0 * processed / Math.Max(1, pairs.Count)));
                    run.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }

            run.Phase = "detecting-suspicious-accounts";
            run.ProgressPercent = 80;
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            foreach (var account in accounts.Where(x => !x.Verified))
            {
                var accountKey = AccountSimilarityEngine.AccountKey(account.UserId);
                if (reviewByKey.TryGetValue(accountKey, out var accountReview) && accountReview.Decision is "ignored" or "verified") continue;
                var suspicious = AccountSimilarityEngine.AnalyzeSuspiciousAccount(account);
                if (suspicious.Score < 24) continue;

                var data = new
                {
                    accountKey,
                    algorithmVersion = run.AlgorithmVersion,
                    account = AccountView(account, now),
                    evidence = suspicious.Evidence,
                    signalCodes = suspicious.Evidence.Select(x => x.Code).ToArray(),
                    score = suspicious.Score,
                };
                findings.Add(new AccountAnalysisFinding
                {
                    RunId = run.Id,
                    FindingKey = accountKey,
                    Kind = "suspicious",
                    Status = "open",
                    Score = suspicious.Score,
                    ModelProbability = suspicious.Score / 100.0,
                    PrimaryUserId = account.UserId,
                    DataJson = JsonSerializer.Serialize(data, JsonOptions),
                });
            }

            run.Phase = "saving-results";
            run.ProgressPercent = 92;
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            db.AccountAnalysisFindings.AddRange(findings);
            run.DuplicateFindings = findings.Count(x => x.Kind == "duplicate");
            run.SuspiciousFindings = findings.Count(x => x.Kind == "suspicious");
            run.Status = "completed";
            run.Phase = "completed";
            run.ProgressPercent = 100;
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Account analysis run {RunId} completed: accounts={Accounts} pairs={Pairs} duplicates={Duplicates} suspicious={Suspicious}",
                run.Id, run.TotalAccounts, run.CandidatePairs, run.DuplicateFindings, run.SuspiciousFindings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.Phase = "failed";
            run.ErrorJson = JsonSerializer.Serialize(new { message = ex.Message, type = ex.GetType().Name }, JsonOptions);
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogError(ex, "Account analysis run {RunId} failed", run.Id);
        }
    }

    private async Task<AccountSnapshotBundle> LoadSnapshotBundleAsync(HashSet<Guid> verifiedIds, CancellationToken ct)
    {
        var warnings = new List<string>();
        var sources = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var identity = await FetchRequiredAsync<SnapshotEnvelope<IdentitySnapshotItem>>(
            "identity", ServiceUrl("IdentityApi", "http://identity-api:8080"),
            "/api/internal/account-intelligence/identity-snapshot?days=365", sources, warnings, ct);

        // The databases share one PostgreSQL host in production. Read the optional snapshots sequentially so a manual
        // analysis never creates a burst of six heavy queries at once.
        var education = await FetchOptionalAsync<EducationSnapshot>(
            "education", ServiceUrl("EducationApi", "http://education-api:8080"),
            "/api/internal/account-intelligence/education-snapshot", sources, warnings, ct) ?? new EducationSnapshot();
        var tasks = (await FetchOptionalAsync<SnapshotEnvelope<TasksSnapshotItem>>(
            "tasks", ServiceUrl("TasksApi", "http://tasks-api:8080"),
            "/api/internal/account-intelligence/tasks-snapshot?days=365", sources, warnings, ct))?.Items ?? [];
        var solutions = (await FetchOptionalAsync<SnapshotEnvelope<SolutionsSnapshotItem>>(
            "solutions", ServiceUrl("SolutionsApi", "http://solutions-api:8080"),
            "/api/internal/account-intelligence/solutions-snapshot?days=365", sources, warnings, ct))?.Items ?? [];
        var observability = (await FetchOptionalAsync<SnapshotEnvelope<ObservabilitySnapshotItem>>(
            "observability", ServiceUrl("ObservabilityApi", "http://observability-api:8080"),
            "/api/internal/account-intelligence/observability-snapshot?days=365", sources, warnings, ct))?.Items ?? [];
        var minecraft = (await FetchOptionalAsync<MinecraftSnapshotEnvelope>(
            "minecraft", ServiceUrl("MinecraftApi", "http://minecraft-api:8080"),
            "/api/internal/account-intelligence/minecraft-snapshot", sources, warnings, ct))?.Items ?? [];

        var groupById = education.Groups.ToDictionary(x => x.GroupId);
        var groupsByUser = education.Memberships
            .GroupBy(x => x.UserId)
            .ToDictionary(
                x => x.Key,
                x => x.Select(m => groupById.GetValueOrDefault(m.GroupId)).Where(g => g != null).Select(g => g!).ToList());
        var tasksByUser = tasks.ToDictionary(x => x.UserId);
        var solutionsByUser = solutions.ToDictionary(x => x.UserId);
        var observabilityByUser = observability.ToDictionary(x => x.UserId);
        var minecraftByUser = minecraft.ToDictionary(x => x.UserId);

        var accounts = identity.Items.Select(item => new AccountIntelligenceAccount
        {
            Identity = item,
            Groups = groupsByUser.GetValueOrDefault(item.UserId) ?? [],
            Tasks = tasksByUser.GetValueOrDefault(item.UserId),
            Solutions = solutionsByUser.GetValueOrDefault(item.UserId),
            Observability = observabilityByUser.GetValueOrDefault(item.UserId),
            Minecraft = minecraftByUser.GetValueOrDefault(item.UserId),
            Verified = verifiedIds.Contains(item.UserId),
        }).ToList();

        return new AccountSnapshotBundle { Accounts = accounts, Sources = sources, Warnings = warnings };
    }

    private List<(AccountIntelligenceAccount A, AccountIntelligenceAccount B)> BuildCandidatePairs(List<AccountIntelligenceAccount> accounts)
    {
        var result = new List<(AccountIntelligenceAccount, AccountIntelligenceAccount)>();
        var pairKeys = new HashSet<string>(StringComparer.Ordinal);

        void AddPair(AccountIntelligenceAccount left, AccountIntelligenceAccount right)
        {
            if (left.UserId == right.UserId) return;
            var key = AccountSimilarityEngine.PairKey(left.UserId, right.UserId);
            if (pairKeys.Add(key)) result.Add((left, right));
        }

        // For normal school-sized installations a complete comparison guarantees that no odd spelling is missed.
        if (accounts.Count <= 1200)
        {
            for (var i = 0; i < accounts.Count; i++)
            for (var j = i + 1; j < accounts.Count; j++)
                AddPair(accounts[i], accounts[j]);
            return result;
        }

        // On large installations use many independent blocking keys, including transliteration, keyboard layout,
        // phonetic skeletons, integrations, groups and technical matches. A missed key in one layer is recovered by others.
        var blocks = new Dictionary<string, List<AccountIntelligenceAccount>>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
        {
            foreach (var key in AccountSimilarityEngine.CandidateBlockKeys(account))
            {
                if (!blocks.TryGetValue(key, out var list)) blocks[key] = list = [];
                list.Add(account);
            }
        }

        foreach (var list in blocks.Values.Where(x => x.Count > 1))
        {
            if (list.Count <= 350)
            {
                for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                    AddPair(list[i], list[j]);
                continue;
            }

            // Very broad blocks (for example a large group) are compared in a wide sorted neighbourhood
            // instead of exploding into millions of weak pairs.
            var ordered = list
                .Select(x => new { Account = x, Key = AccountSimilarityEngine.CandidateSortKeys(x).FirstOrDefault() ?? x.UserId.ToString("N") })
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            for (var j = i + 1; j < Math.Min(ordered.Count, i + 31); j++)
                AddPair(ordered[i].Account, ordered[j].Account);
        }

        // Global sorted-neighbour recovery catches one-character mistakes that happened to cross block boundaries.
        var sortRows = accounts
            .SelectMany(account => AccountSimilarityEngine.CandidateSortKeys(account).Select(key => new { Account = account, Key = key }))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < sortRows.Count; i++)
        {
            for (var j = i + 1; j < Math.Min(sortRows.Count, i + 13); j++)
            {
                if (sortRows[i].Key[0] != sortRows[j].Key[0]) break;
                AddPair(sortRows[i].Account, sortRows[j].Account);
            }
        }

        return result;
    }

    private static void AddConfirmedReviewPairs(
        List<(AccountIntelligenceAccount A, AccountIntelligenceAccount B)> pairs,
        List<AccountIntelligenceAccount> accounts,
        List<AccountAnalysisReview> reviews)
    {
        var byId = accounts.ToDictionary(x => x.UserId);
        var known = pairs.Select(x => AccountSimilarityEngine.PairKey(x.A.UserId, x.B.UserId)).ToHashSet(StringComparer.Ordinal);
        foreach (var review in reviews.Where(x => x.SubjectType == "pair" && x.Decision == "duplicate"))
        {
            var parts = review.SubjectKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !string.Equals(parts[0], "pair", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Guid.TryParseExact(parts[1], "N", out var leftId) || !Guid.TryParseExact(parts[2], "N", out var rightId)) continue;
            if (!byId.TryGetValue(leftId, out var left) || !byId.TryGetValue(rightId, out var right)) continue;
            if (known.Add(AccountSimilarityEngine.PairKey(leftId, rightId))) pairs.Add((left, right));
        }
    }

    private static object AccountView(AccountIntelligenceAccount account, DateTimeOffset now)
    {
        var last = AccountSimilarityEngine.LastActivity(account);
        return new
        {
            userId = account.UserId,
            displayName = account.DisplayName,
            account.Identity.Login,
            account.Identity.Email,
            account.Identity.FirstName,
            account.Identity.LastName,
            account.Identity.PhoneNumber,
            account.Identity.ProfilePictureUrl,
            account.Identity.Role,
            account.Identity.CreatedAt,
            account.Identity.LastLoginAt,
            account.Identity.TelegramUsername,
            account.Identity.TelegramLinkedAtUtc,
            account.Identity.Location,
            account.Identity.Education,
            verified = account.Verified,
            groups = account.Groups.Select(x => new { x.GroupId, x.Name, x.Code }).ToArray(),
            minecraft = account.Minecraft?.Links.Select(x => new { x.PlayerName, x.PlayerUuid, x.LinkedAtUtc }).ToArray() ?? [],
            lastActivityAt = last,
            activityScore = AccountSimilarityEngine.ActivityScore(account, now),
            historicalValueScore = AccountSimilarityEngine.HistoricalValueScore(account),
            totalMeaningfulActions = AccountSimilarityEngine.TotalMeaningfulActions(account),
            solvedCount = (account.Solutions?.SolvedCount ?? 0) + (account.Tasks?.PassedAttempts ?? 0),
            totalScore = account.Solutions?.TotalScore ?? 0,
            loginCount = account.Identity.LoginCount,
            activeDays = account.Identity.LoginDays + (account.Tasks?.ActiveDays ?? 0) + (account.Solutions?.ActiveDays ?? 0) + (account.Observability?.ActiveDays ?? 0),
        };
    }

    private static AccountLearningProfile BuildLearningProfile(List<AccountAnalysisReview> reviews)
    {
        var labeled = reviews.Where(x => x.SubjectType == "pair" && x.Decision is "duplicate" or "different" && !string.IsNullOrWhiteSpace(x.SignalsJson)).ToList();
        var counts = new Dictionary<string, (int Positive, int Negative)>(StringComparer.OrdinalIgnoreCase);
        foreach (var review in labeled)
        {
            string[] signals;
            try { signals = JsonSerializer.Deserialize<string[]>(review.SignalsJson!, JsonOptions) ?? []; }
            catch { continue; }
            foreach (var signal in signals.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var current = counts.GetValueOrDefault(signal);
                counts[signal] = review.Decision == "duplicate"
                    ? (current.Positive + 1, current.Negative)
                    : (current.Positive, current.Negative + 1);
            }
        }

        var adjustments = counts.ToDictionary(
            x => x.Key,
            x =>
            {
                var reliability = (x.Value.Positive + 2.0) / (x.Value.Positive + x.Value.Negative + 4.0);
                var support = Math.Min(1.0, (x.Value.Positive + x.Value.Negative) / 20.0);
                return (reliability - 0.5) * 18.0 * support;
            },
            StringComparer.OrdinalIgnoreCase);
        return new AccountLearningProfile { LabelCount = labeled.Count, SignalAdjustments = adjustments };
    }

    private string ServiceUrl(string name, string fallback)
        => (configuration[$"Services:{name}"] ?? configuration[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');

    private async Task<T> FetchRequiredAsync<T>(string source, string baseUrl, string path, Dictionary<string, bool> sources, List<string> warnings, CancellationToken ct)
    {
        var value = await FetchOptionalAsync<T>(source, baseUrl, path, sources, warnings, ct);
        return value ?? throw new InvalidOperationException($"Обязательный источник {source} недоступен.");
    }

    private async Task<T?> FetchOptionalAsync<T>(string source, string baseUrl, string path, Dictionary<string, bool> sources, List<string> warnings, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            var key = configuration["AccountIntelligence:InternalApiKey"]
                ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY")
                ?? configuration["InternalApi:Key"]
                ?? configuration["TaskForgeInternalApi:ApiKey"]
                ?? Environment.GetEnvironmentVariable("TASKFORGE_AGENT_INTERNAL_KEY");
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{source} returned {(int)response.StatusCode}");
            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
            sources[source] = value != null;
            if (value == null) warnings.Add($"Источник {source} вернул пустой ответ.");
            return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sources[source] = false;
            warnings.Add($"Источник {source} недоступен: {ex.Message}");
            logger.LogWarning(ex, "Account intelligence source {Source} is unavailable", source);
            return default;
        }
    }
}
