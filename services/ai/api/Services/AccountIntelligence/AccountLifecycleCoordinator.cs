using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;

namespace TaskForge.Ai.Api.Services.AccountIntelligence;

internal sealed class AccountLifecycleCoordinator(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<AccountLifecycleCoordinator> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    internal sealed class IdentityAccountState
    {
        public Guid UserId { get; set; }
        public string? Login { get; set; }
        public string? Email { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? DisplayName { get; set; }
        public string? Role { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? LastLoginAt { get; set; }
        public string? AccountStatus { get; set; }
        public Guid? MergedIntoUserId { get; set; }
        public bool Blocked { get; set; }
        public string? BlockReason { get; set; }
        public string? BlockNote { get; set; }
        public DateTimeOffset? BlockedAtUtc { get; set; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }
        public DateTimeOffset? ExpiresAtUtc { get; set; }
    }

    private sealed record LifecycleMutation(
        Guid OperationId,
        Guid SourceUserId,
        Guid? TargetUserId,
        Guid ActorUserId,
        string? Reason,
        bool HardDelete = false);

    private sealed record BlockMutation(
        Guid UserId,
        Guid ActorUserId,
        string? Reason,
        string? Note,
        DateTimeOffset? ExpiresAtUtc);

    private sealed record UnblockMutation(Guid UserId, Guid ActorUserId);

    private sealed class OperationOptions
    {
        public bool HardDelete { get; set; }
        public string? Note { get; set; }
        public DateTimeOffset? ExpiresAtUtc { get; set; }
    }

    private sealed class StepState
    {
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = "queued";
        public int Progress { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public JsonElement? Result { get; set; }
        public string? Error { get; set; }
    }

    private sealed record OperationStep(string Key, string Title, string ServiceName, string FallbackBaseUrl, string Path, object Payload);

    public async Task<IdentityAccountState?> GetIdentityAccountAsync(Guid userId, CancellationToken ct)
        => await SendAsync<IdentityAccountState>(
            HttpMethod.Get,
            ServiceUrl("IdentityApi", "http://identity-api:8080") + $"/api/internal/account-lifecycle/users/{userId}",
            null,
            ct,
            allowNotFound: true);

    public async Task<List<JsonElement>> GetBlockedAccountsAsync(string? query, int take, CancellationToken ct)
    {
        var url = ServiceUrl("IdentityApi", "http://identity-api:8080")
            + $"/api/internal/account-lifecycle/blocked-accounts?take={Math.Clamp(take, 1, 2000)}";
        if (!string.IsNullOrWhiteSpace(query)) url += "&q=" + Uri.EscapeDataString(query.Trim());
        return await SendAsync<List<JsonElement>>(HttpMethod.Get, url, null, ct) ?? [];
    }

    public async Task ProcessOperationAsync(Guid operationId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        var operation = await db.AccountManagementOperations.FirstOrDefaultAsync(x => x.Id == operationId, ct);
        if (operation == null) return;

        try
        {
            operation.Status = "running";
            operation.Phase = "starting";
            operation.ProgressPercent = 1;
            operation.AttemptCount++;
            operation.StartedAtUtc ??= DateTimeOffset.UtcNow;
            operation.CompletedAtUtc = null;
            operation.ErrorJson = null;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var options = ParseOptions(operation.OptionsJson);
            var steps = BuildSteps(operation, options);
            var states = ParseSteps(operation.StepsJson, steps);
            operation.StepsJson = JsonSerializer.Serialize(states, JsonOptions);
            await db.SaveChangesAsync(ct);

            for (var index = 0; index < steps.Count; index++)
            {
                await db.Entry(operation).ReloadAsync(ct);
                if (operation.CancelRequested)
                {
                    operation.Status = "cancelled";
                    operation.Phase = "cancelled";
                    operation.CompletedAtUtc = DateTimeOffset.UtcNow;
                    operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return;
                }

                var step = steps[index];
                var state = states[index];
                if (string.Equals(state.Status, "completed", StringComparison.OrdinalIgnoreCase))
                    continue;

                state.Status = "running";
                state.StartedAtUtc = DateTimeOffset.UtcNow;
                state.Error = null;
                operation.Phase = step.Key;
                operation.ProgressPercent = Math.Max(2, (int)Math.Floor(index * 100.0 / Math.Max(1, steps.Count)));
                operation.StepsJson = JsonSerializer.Serialize(states, JsonOptions);
                operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);

                try
                {
                    JsonElement result;
                    if (step.ServiceName == "ai-local")
                    {
                        result = await ApplyLocalAiMutationAsync(db, operation, ct);
                    }
                    else
                    {
                        var url = ServiceUrl(step.ServiceName, step.FallbackBaseUrl) + step.Path;
                        result = await SendAsync<JsonElement>(HttpMethod.Post, url, step.Payload, ct);
                    }
                    state.Status = "completed";
                    state.Progress = 100;
                    state.CompletedAtUtc = DateTimeOffset.UtcNow;
                    state.Result = result;
                    operation.ProgressPercent = Math.Min(99, (int)Math.Floor((index + 1) * 100.0 / Math.Max(1, steps.Count)));
                    operation.StepsJson = JsonSerializer.Serialize(states, JsonOptions);
                    operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    state.Status = "failed";
                    state.Error = ex.Message;
                    state.CompletedAtUtc = DateTimeOffset.UtcNow;
                    operation.Status = "failed";
                    operation.Phase = step.Key;
                    operation.ErrorJson = JsonSerializer.Serialize(new
                    {
                        message = "Операция остановлена на шаге: " + step.Title,
                        step = step.Key,
                        detail = ex.Message,
                    }, JsonOptions);
                    operation.StepsJson = JsonSerializer.Serialize(states, JsonOptions);
                    operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    operation.CompletedAtUtc = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    logger.LogError(ex, "Account lifecycle operation {OperationId} failed at {Step}", operation.Id, step.Key);
                    return;
                }
            }

            operation.Status = "completed";
            operation.Phase = "completed";
            operation.ProgressPercent = 100;
            operation.ResultJson = JsonSerializer.Serialize(new
            {
                operation.Type,
                operation.SourceUserId,
                operation.TargetUserId,
                completedAtUtc = DateTimeOffset.UtcNow,
            }, JsonOptions);
            operation.CompletedAtUtc = DateTimeOffset.UtcNow;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            operation.Status = "failed";
            operation.Phase = "failed";
            operation.ErrorJson = JsonSerializer.Serialize(new { message = ex.Message }, JsonOptions);
            operation.CompletedAtUtc = DateTimeOffset.UtcNow;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogError(ex, "Account lifecycle operation {OperationId} failed", operation.Id);
        }
    }

    private List<OperationStep> BuildSteps(AccountManagementOperation operation, OperationOptions options)
    {
        var mutation = new LifecycleMutation(
            operation.Id,
            operation.SourceUserId,
            operation.TargetUserId,
            operation.RequestedByUserId,
            operation.Reason,
            options.HardDelete);

        if (operation.Type == "block")
        {
            return
            [
                new("identity-block", "Блокировка аккаунта", "IdentityApi", "http://identity-api:8080", "/api/internal/account-lifecycle/block",
                    new BlockMutation(operation.SourceUserId, operation.RequestedByUserId, operation.Reason, options.Note, options.ExpiresAtUtc)),
            ];
        }
        if (operation.Type == "unblock")
        {
            return
            [
                new("identity-unblock", "Снятие блокировки", "IdentityApi", "http://identity-api:8080", "/api/internal/account-lifecycle/unblock",
                    new UnblockMutation(operation.SourceUserId, operation.RequestedByUserId)),
            ];
        }

        var action = operation.Type == "merge" ? "merge" : "delete";
        var steps = new List<OperationStep>
        {
            new($"identity-prepare-{action}", operation.Type == "merge" ? "Проверка и блокировка дубля" : "Проверка и блокировка аккаунта",
                "IdentityApi", "http://identity-api:8080", $"/api/internal/account-lifecycle/prepare-{action}", mutation),
            new($"education-{action}", "Учебные группы и владельцы курсов", "EducationApi", "http://education-api:8080", $"/api/internal/account-lifecycle/{action}", mutation),
            new($"tasks-{action}", "Попытки, сессии и история работы", "TasksApi", "http://tasks-api:8080", $"/api/internal/account-lifecycle/{action}", mutation),
            new($"quiz-{action}", "Quiz-попытки и прогресс", "QuizApi", "http://quiz-api:8080", $"/api/internal/account-lifecycle/{action}", mutation),
            new($"solutions-{action}", "Решения, рейтинг, награды и энергия", "SolutionsApi", "http://solutions-api:8080", $"/api/internal/account-lifecycle/{action}", mutation),
            new($"observability-{action}", "История активности", "ObservabilityApi", "http://observability-api:8080", $"/api/internal/account-lifecycle/{action}", mutation),
            new($"minecraft-{action}", "Minecraft-привязки и баланс", "MinecraftApi", "http://minecraft-api:8080", $"/api/internal/account-lifecycle/{action}", mutation),
            new($"support-{action}", "Поддержка", "SupportApi", "http://support-api:8080", $"/api/internal/account-lifecycle/{action}", mutation),
            new($"notifications-{action}", "Уведомления", "NotificationsApi", "http://notifications-api:8080", $"/api/internal/account-lifecycle/{action}", mutation),
            new($"execution-{action}", "История запусков кода", "ExecutionApi", "http://execution-api:8080", $"/api/internal/account-lifecycle/{action}", mutation),
            new($"ai-{action}", "Решения менеджера и AI-история", "ai-local", string.Empty, string.Empty, mutation),
            new($"identity-finalize-{action}", operation.Type == "merge" ? "Завершение объединения" : "Завершение удаления",
                "IdentityApi", "http://identity-api:8080", $"/api/internal/account-lifecycle/finalize-{action}", mutation),
        };
        return steps;
    }

    private static List<StepState> ParseSteps(string? json, List<OperationStep> definitions)
    {
        List<StepState> prior;
        try { prior = string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<StepState>>(json, JsonOptions) ?? []; }
        catch { prior = []; }
        var byKey = prior.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        return definitions.Select((definition, index) => byKey.TryGetValue(definition.Key, out var state)
            ? state
            : new StepState { Key = definition.Key, Title = definition.Title, Progress = 0 }).ToList();
    }

    private async Task<JsonElement> ApplyLocalAiMutationAsync(AiDbContext db, AccountManagementOperation operation, CancellationToken ct)
    {
        var source = operation.SourceUserId;
        var target = operation.TargetUserId;
        var changed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (operation.Type == "merge" && target.HasValue)
        {
            var conversations = await db.Conversations.Where(x => x.UserId == source).ToListAsync(ct);
            foreach (var row in conversations) row.UserId = target.Value;
            changed["conversations"] = conversations.Count;

            var sourceAccountReview = await db.AccountAnalysisReviews.FirstOrDefaultAsync(x => x.SubjectType == "account" && x.UserId == source, ct);
            var targetAccountReview = await db.AccountAnalysisReviews.FirstOrDefaultAsync(x => x.SubjectType == "account" && x.UserId == target.Value, ct);
            if (sourceAccountReview != null)
            {
                if (targetAccountReview == null)
                {
                    sourceAccountReview.UserId = target.Value;
                    sourceAccountReview.SubjectKey = AccountSimilarityEngine.AccountKey(target.Value);
                    sourceAccountReview.UpdatedAtUtc = DateTimeOffset.UtcNow;
                }
                else
                {
                    db.AccountAnalysisReviews.Remove(sourceAccountReview);
                }
            }

            var findings = await db.AccountAnalysisFindings
                .Where(x => x.PrimaryUserId == source || x.SecondaryUserId == source)
                .ToListAsync(ct);
            foreach (var finding in findings)
            {
                finding.Status = "resolved-by-merge";
                finding.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
            changed["findings"] = findings.Count;
        }
        else if (operation.Type == "delete")
        {
            var conversations = await db.Conversations.Where(x => x.UserId == source).ToListAsync(ct);
            foreach (var row in conversations) row.UserId = null;
            changed["conversationsAnonymized"] = conversations.Count;

            var reviews = await db.AccountAnalysisReviews.Where(x => x.UserId == source || x.OtherUserId == source).ToListAsync(ct);
            db.AccountAnalysisReviews.RemoveRange(reviews);
            changed["reviewsDeleted"] = reviews.Count;

            var findings = await db.AccountAnalysisFindings
                .Where(x => x.PrimaryUserId == source || x.SecondaryUserId == source)
                .ToListAsync(ct);
            foreach (var finding in findings)
            {
                finding.Status = "resolved-by-delete";
                finding.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
            changed["findings"] = findings.Count;
        }

        await db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(changed, JsonOptions);
    }

    private OperationOptions ParseOptions(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new OperationOptions() : JsonSerializer.Deserialize<OperationOptions>(json, JsonOptions) ?? new OperationOptions(); }
        catch { return new OperationOptions(); }
    }

    private string ServiceUrl(string name, string fallback)
        => (configuration[$"Services:{name}"] ?? configuration[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, object? payload, CancellationToken ct, bool allowNotFound = false)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(120);
        using var request = new HttpRequestMessage(method, url);
        var key = configuration["AccountIntelligence:InternalApiKey"]
            ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY")
            ?? configuration["InternalApi:Key"]
            ?? configuration["TaskForgeInternalApi:ApiKey"]
            ?? Environment.GetEnvironmentVariable("TASKFORGE_AGENT_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
        if (payload != null) request.Content = JsonContent.Create(payload, options: JsonOptions);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (allowNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound) return default;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var message = body.Length > 1200 ? body[..1200] : body;
            throw new InvalidOperationException($"{method} {url} returned {(int)response.StatusCode}: {message}");
        }
        if (response.Content.Headers.ContentLength == 0) return default;
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }
}
