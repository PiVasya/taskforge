using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;
using TaskForge.Ai.Api.Services.AccountIntelligence;

namespace TaskForge.Ai.Api.Endpoints;

internal static partial class AiApiEndpoints
{
    private static readonly JsonSerializerOptions AccountLifecycleJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record AccountOperationCreateRequest(
        string? Type,
        Guid SourceUserId,
        Guid? TargetUserId,
        Guid? FindingId,
        string? Reason,
        string? Note,
        string? Confirmation,
        bool HardDelete = false,
        DateTimeOffset? ExpiresAtUtc = null);

    private sealed record AccountOperationUpdateRequest(string? Reason, string? Note, DateTimeOffset? ExpiresAtUtc, bool? HardDelete, bool ClearExpiresAt = false);
    private sealed record AccountBlockApiRequest(string? Reason, string? Note, DateTimeOffset? ExpiresAtUtc);

    private static WebApplication MapAccountLifecycleEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/ai/account-manager/blocked-accounts", async (
            AccountLifecycleCoordinator coordinator,
            string? q,
            int take = 500,
            CancellationToken ct = default) =>
        {
            var rows = await coordinator.GetBlockedAccountsAsync(q, take, ct);
            return Results.Ok(rows);
        });

        app.MapPut("/api/admin/ai/account-manager/accounts/{userId:guid}/block", async (
            Guid userId,
            AccountBlockApiRequest request,
            HttpContext http,
            IConfiguration cfg,
            AiDbContext db,
            AccountLifecycleCoordinator coordinator,
            CancellationToken ct) =>
        {
            var actor = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!actor.HasValue) return Results.Unauthorized();
            var source = await coordinator.GetIdentityAccountAsync(userId, ct);
            if (source == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            if (source.UserId == actor.Value) return Results.BadRequest(new { message = "Нельзя заблокировать собственный аккаунт.", code = "CANNOT_BLOCK_SELF" });
            if (string.Equals(source.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Администраторский аккаунт защищён.", code = "ADMIN_ACCOUNT_PROTECTED" });
            if (!string.Equals(source.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { message = "Можно блокировать только активный аккаунт.", code = "ACCOUNT_NOT_ACTIVE" });

            var operation = await CreateOperationAsync(db, new AccountOperationCreateRequest(
                "block", userId, null, null, request.Reason, request.Note, null, false, request.ExpiresAtUtc), actor.Value, source, null, ct);
            if (operation == null)
                return Results.Conflict(new { message = "Для аккаунта уже выполняется операция управления.", code = "ACCOUNT_OPERATION_ALREADY_ACTIVE" });
            return Results.Accepted($"/api/admin/ai/account-manager/operations/{operation.Id}", ToAccountOperationDto(operation));
        });

        app.MapDelete("/api/admin/ai/account-manager/accounts/{userId:guid}/block", async (
            Guid userId,
            HttpContext http,
            IConfiguration cfg,
            AiDbContext db,
            AccountLifecycleCoordinator coordinator,
            CancellationToken ct) =>
        {
            var actor = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!actor.HasValue) return Results.Unauthorized();
            var source = await coordinator.GetIdentityAccountAsync(userId, ct);
            if (source == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            if (source.UserId == actor.Value) return Results.BadRequest(new { message = "Нельзя менять собственную блокировку.", code = "CANNOT_UNBLOCK_SELF" });
            if (!string.Equals(source.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { message = "Удалённый или объединённый аккаунт нельзя разблокировать.", code = "ACCOUNT_NOT_ACTIVE" });
            var operation = await CreateOperationAsync(db, new AccountOperationCreateRequest(
                "unblock", userId, null, null, "Разблокировка администратором", null, null), actor.Value, source, null, ct);
            if (operation == null)
                return Results.Conflict(new { message = "Для аккаунта уже выполняется операция управления.", code = "ACCOUNT_OPERATION_ALREADY_ACTIVE" });
            return Results.Accepted($"/api/admin/ai/account-manager/operations/{operation.Id}", ToAccountOperationDto(operation));
        });

        app.MapPost("/api/admin/ai/account-manager/operations", async (
            AccountOperationCreateRequest request,
            HttpContext http,
            IConfiguration cfg,
            AiDbContext db,
            AccountLifecycleCoordinator coordinator,
            CancellationToken ct) =>
        {
            var actor = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!actor.HasValue) return Results.Unauthorized();
            var type = NormalizeOperationType(request.Type);
            if (type is not ("merge" or "delete" or "block" or "unblock"))
                return Results.BadRequest(new { message = "Допустимые операции: merge, delete, block, unblock.", code = "INVALID_ACCOUNT_OPERATION" });

            var source = await coordinator.GetIdentityAccountAsync(request.SourceUserId, ct);
            if (source == null) return Results.NotFound(new { message = "Исходный аккаунт не найден.", code = "USER_NOT_FOUND" });
            if (source.UserId == actor.Value)
                return Results.BadRequest(new { message = "Нельзя выполнять операции жизненного цикла над собственным аккаунтом.", code = "CANNOT_MANAGE_SELF" });
            if ((type is "merge" or "delete" or "block") && string.Equals(source.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Администраторский аккаунт защищён.", code = "ADMIN_ACCOUNT_PROTECTED" });
            if ((type is "merge" or "delete" or "block") && !string.Equals(source.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { message = "Операция доступна только для активного аккаунта.", code = "ACCOUNT_NOT_ACTIVE" });
            AccountLifecycleCoordinator.IdentityAccountState? target = null;
            if (type == "merge")
            {
                if (!request.TargetUserId.HasValue || request.TargetUserId == request.SourceUserId)
                    return Results.BadRequest(new { message = "Выберите другой основной аккаунт.", code = "INVALID_MERGE_TARGET" });
                target = await coordinator.GetIdentityAccountAsync(request.TargetUserId.Value, ct);
                if (target == null) return Results.NotFound(new { message = "Основной аккаунт не найден.", code = "USER_NOT_FOUND" });
                if (target.UserId == actor.Value || string.Equals(target.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { message = "Администраторский аккаунт нельзя использовать в объединении.", code = "ADMIN_ACCOUNT_PROTECTED" });
                if (!string.Equals(target.AccountStatus, "active", StringComparison.OrdinalIgnoreCase) || target.Blocked)
                    return Results.Conflict(new { message = "Основной аккаунт должен быть активен и не заблокирован.", code = "MERGE_TARGET_UNAVAILABLE" });
            }

            if (type is "merge" or "delete")
            {
                var confirmation = request.Confirmation?.Trim();
                var accepted = new[] { source.UserId.ToString(), source.Login, source.DisplayName }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Any(x => string.Equals(x, confirmation, StringComparison.OrdinalIgnoreCase));
                if (!accepted)
                {
                    return Results.BadRequest(new
                    {
                        message = "Подтверждение не совпало с логином, именем или ID удаляемого аккаунта.",
                        code = "ACCOUNT_CONFIRMATION_MISMATCH"
                    });
                }
            }

            var operation = await CreateOperationAsync(db, request with { Type = type }, actor.Value, source, target, ct);
            if (operation == null)
                return Results.Conflict(new { message = "Для одного из аккаунтов уже выполняется операция управления.", code = "ACCOUNT_OPERATION_ALREADY_ACTIVE" });

            if (type == "merge" && request.FindingId.HasValue)
            {
                var finding = await db.AccountAnalysisFindings.FirstOrDefaultAsync(x => x.Id == request.FindingId.Value, ct);
                if (finding != null)
                {
                    var review = await db.AccountAnalysisReviews.FirstOrDefaultAsync(x => x.SubjectType == "pair" && x.SubjectKey == finding.FindingKey, ct);
                    if (review == null)
                    {
                        review = new AccountAnalysisReview { SubjectType = "pair", SubjectKey = finding.FindingKey };
                        db.AccountAnalysisReviews.Add(review);
                    }
                    review.UserId = finding.PrimaryUserId;
                    review.OtherUserId = finding.SecondaryUserId;
                    review.Decision = "duplicate";
                    review.Note = CleanNote(request.Reason);
                    review.SignalsJson = ExtractSignalCodes(finding.DataJson);
                    review.DataJson = finding.DataJson;
                    review.ReviewedByUserId = actor.Value;
                    review.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    finding.Status = "merge-queued";
                    finding.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }

            return Results.Accepted($"/api/admin/ai/account-manager/operations/{operation.Id}", ToAccountOperationDto(operation));
        });

        app.MapGet("/api/admin/ai/account-manager/operations", async (
            AiDbContext db,
            string? status,
            string? type,
            Guid? userId,
            bool archived = false,
            int take = 100,
            CancellationToken ct = default) =>
        {
            take = Math.Clamp(take, 1, 500);
            var query = db.AccountManagementOperations.AsNoTracking()
                .Where(x => archived ? x.ArchivedAtUtc != null : x.ArchivedAtUtc == null);
            if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.Type == type.Trim().ToLowerInvariant());
            if (userId.HasValue) query = query.Where(x => x.SourceUserId == userId || x.TargetUserId == userId);
            var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Take(take).ToListAsync(ct);
            return Results.Ok(rows.Select(ToAccountOperationDto).ToList());
        });

        app.MapGet("/api/admin/ai/account-manager/operations/{operationId:guid}", async (
            Guid operationId,
            AiDbContext db,
            CancellationToken ct) =>
        {
            var operation = await db.AccountManagementOperations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == operationId, ct);
            return operation == null
                ? Results.NotFound(new { message = "Операция не найдена.", code = "ACCOUNT_OPERATION_NOT_FOUND" })
                : Results.Ok(ToAccountOperationDto(operation));
        });

        app.MapPut("/api/admin/ai/account-manager/operations/{operationId:guid}", async (
            Guid operationId,
            AccountOperationUpdateRequest request,
            AiDbContext db,
            CancellationToken ct) =>
        {
            var operation = await db.AccountManagementOperations.FirstOrDefaultAsync(x => x.Id == operationId, ct);
            if (operation == null) return Results.NotFound(new { message = "Операция не найдена.", code = "ACCOUNT_OPERATION_NOT_FOUND" });
            if (operation.Status is not ("queued" or "failed" or "cancelled"))
                return Results.Conflict(new { message = "Изменять можно только ожидающую, отменённую или неудачную операцию.", code = "ACCOUNT_OPERATION_NOT_EDITABLE" });

            operation.Reason = CleanNote(request.Reason);
            var options = ParseOperationOptions(operation.OptionsJson);
            options["note"] = JsonSerializer.SerializeToElement(CleanNote(request.Note));
            if (request.ClearExpiresAt) options["expiresAtUtc"] = JsonSerializer.SerializeToElement<DateTimeOffset?>(null);
            else if (request.ExpiresAtUtc.HasValue) options["expiresAtUtc"] = JsonSerializer.SerializeToElement(request.ExpiresAtUtc.Value);
            if (request.HardDelete.HasValue) options["hardDelete"] = JsonSerializer.SerializeToElement(request.HardDelete.Value);
            operation.OptionsJson = JsonSerializer.Serialize(options, AccountLifecycleJsonOptions);
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToAccountOperationDto(operation));
        });

        app.MapPost("/api/admin/ai/account-manager/operations/{operationId:guid}/retry", async (
            Guid operationId,
            AiDbContext db,
            CancellationToken ct) =>
        {
            var operation = await db.AccountManagementOperations.FirstOrDefaultAsync(x => x.Id == operationId, ct);
            if (operation == null) return Results.NotFound(new { message = "Операция не найдена.", code = "ACCOUNT_OPERATION_NOT_FOUND" });
            if (operation.Status is not ("failed" or "cancelled"))
                return Results.Conflict(new { message = "Повторить можно только неудачную или отменённую операцию.", code = "ACCOUNT_OPERATION_NOT_RETRYABLE" });
            operation.Status = "queued";
            operation.Phase = "queued-for-retry";
            operation.CancelRequested = false;
            operation.ErrorJson = null;
            operation.CompletedAtUtc = null;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/admin/ai/account-manager/operations/{operation.Id}", ToAccountOperationDto(operation));
        });

        app.MapPost("/api/admin/ai/account-manager/operations/{operationId:guid}/cancel", async (
            Guid operationId,
            AiDbContext db,
            CancellationToken ct) =>
        {
            var operation = await db.AccountManagementOperations.FirstOrDefaultAsync(x => x.Id == operationId, ct);
            if (operation == null) return Results.NotFound(new { message = "Операция не найдена.", code = "ACCOUNT_OPERATION_NOT_FOUND" });
            if (operation.Status == "cancelled")
                return Results.Ok(ToAccountOperationDto(operation));
            if (operation.Status != "queued")
                return Results.Conflict(new
                {
                    message = "После начала переноса данных операцию нельзя отменить: это могло бы оставить аккаунты в частично объединённом состоянии. Дождитесь завершения или исправьте ошибку и повторите операцию.",
                    code = "ACCOUNT_OPERATION_ALREADY_STARTED"
                });
            operation.CancelRequested = true;
            operation.Status = "cancelled";
            operation.Phase = "cancelled";
            operation.CompletedAtUtc = DateTimeOffset.UtcNow;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToAccountOperationDto(operation));
        });

        app.MapDelete("/api/admin/ai/account-manager/operations/{operationId:guid}", async (
            Guid operationId,
            AiDbContext db,
            CancellationToken ct) =>
        {
            var operation = await db.AccountManagementOperations.FirstOrDefaultAsync(x => x.Id == operationId, ct);
            if (operation == null) return Results.NotFound(new { message = "Операция не найдена.", code = "ACCOUNT_OPERATION_NOT_FOUND" });
            if (operation.Status is "running" or "starting" or "queued")
                return Results.Conflict(new { message = "Сначала завершите или отмените операцию.", code = "ACCOUNT_OPERATION_ACTIVE" });
            operation.ArchivedAtUtc = DateTimeOffset.UtcNow;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { archived = true, operation = ToAccountOperationDto(operation) });
        });

        app.MapPost("/api/admin/ai/account-manager/operations/{operationId:guid}/restore", async (
            Guid operationId,
            AiDbContext db,
            CancellationToken ct) =>
        {
            var operation = await db.AccountManagementOperations.FirstOrDefaultAsync(x => x.Id == operationId, ct);
            if (operation == null) return Results.NotFound(new { message = "Операция не найдена.", code = "ACCOUNT_OPERATION_NOT_FOUND" });
            operation.ArchivedAtUtc = null;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToAccountOperationDto(operation));
        });

        return app;
    }

    private static async Task<AccountManagementOperation?> CreateOperationAsync(
        AiDbContext db,
        AccountOperationCreateRequest request,
        Guid actorUserId,
        AccountLifecycleCoordinator.IdentityAccountState source,
        AccountLifecycleCoordinator.IdentityAccountState? target,
        CancellationToken ct)
    {
        var type = NormalizeOperationType(request.Type)!;
        var active = await db.AccountManagementOperations.AsNoTracking()
            .Where(x => x.ArchivedAtUtc == null && (x.Status == "queued" || x.Status == "starting" || x.Status == "running"))
            .AnyAsync(x => x.SourceUserId == source.UserId || x.TargetUserId == source.UserId ||
                (target != null && (x.SourceUserId == target.UserId || x.TargetUserId == target.UserId)), ct);
        if (active) return null;

        var operation = new AccountManagementOperation
        {
            Type = type,
            Status = "queued",
            Phase = "queued",
            SourceUserId = source.UserId,
            TargetUserId = target?.UserId,
            RequestedByUserId = actorUserId,
            Reason = CleanNote(request.Reason),
            OptionsJson = JsonSerializer.Serialize(new
            {
                hardDelete = request.HardDelete,
                note = CleanNote(request.Note),
                expiresAtUtc = request.ExpiresAtUtc,
            }, AccountLifecycleJsonOptions),
            SourceSnapshotJson = JsonSerializer.Serialize(source, AccountLifecycleJsonOptions),
            TargetSnapshotJson = target == null ? null : JsonSerializer.Serialize(target, AccountLifecycleJsonOptions),
        };
        db.AccountManagementOperations.Add(operation);
        await db.SaveChangesAsync(ct);
        return operation;
    }

    private static object ToAccountOperationDto(AccountManagementOperation operation) => new
    {
        operation.Id,
        operation.Type,
        operation.Status,
        operation.Phase,
        operation.ProgressPercent,
        operation.SourceUserId,
        operation.TargetUserId,
        operation.RequestedByUserId,
        operation.Reason,
        options = ParseAccountJson(operation.OptionsJson),
        source = ParseAccountJson(operation.SourceSnapshotJson),
        target = ParseAccountJson(operation.TargetSnapshotJson),
        steps = ParseAccountJson(operation.StepsJson),
        result = ParseAccountJson(operation.ResultJson),
        error = ParseAccountJson(operation.ErrorJson),
        operation.AttemptCount,
        operation.CancelRequested,
        operation.StartedAtUtc,
        operation.CompletedAtUtc,
        operation.ArchivedAtUtc,
        operation.CreatedAtUtc,
        operation.UpdatedAtUtc,
    };

    private static Dictionary<string, JsonElement> ParseOperationOptions(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new(); }
        catch { return new(); }
    }

    private static string? NormalizeOperationType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "merge" or "combine" => "merge",
        "delete" or "remove" => "delete",
        "block" or "ban" => "block",
        "unblock" or "unban" => "unblock",
        _ => null,
    };
}
