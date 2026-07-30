using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;
using TaskForge.Identity.Api.Services.AccountLifecycle;

namespace TaskForge.Identity.Api.Endpoints;

internal static partial class IdentityApiEndpoints
{
    private sealed record AccountBlockRequest(
        Guid UserId,
        Guid ActorUserId,
        string? Reason,
        string? Note,
        DateTimeOffset? ExpiresAtUtc);

    private sealed record AccountUnblockRequest(Guid UserId, Guid ActorUserId);

    private sealed record AccountLifecycleRequest(
        Guid OperationId,
        Guid SourceUserId,
        Guid? TargetUserId,
        Guid ActorUserId,
        string? Reason,
        bool HardDelete = false);

    private static WebApplication MapAccountLifecycleInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/account-lifecycle/users/{userId:guid}", async (
            Guid userId,
            IdentityDbContext db,
            CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            var block = await db.BlockedAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
            return Results.Ok(ToLifecycleUserDto(user, block));
        });

        app.MapGet("/api/internal/account-lifecycle/blocked-accounts", async (
            IdentityDbContext db,
            string? q,
            int take = 500,
            CancellationToken ct = default) =>
        {
            take = Math.Clamp(take, 1, 2000);
            var now = DateTimeOffset.UtcNow;
            var query = from block in db.BlockedAccounts.AsNoTracking()
                        join user in db.Users.AsNoTracking() on block.UserId equals user.Id
                        where !block.ExpiresAtUtc.HasValue || block.ExpiresAtUtc > now
                        select new { block, user };
            if (!string.IsNullOrWhiteSpace(q))
            {
                var search = q.Trim().ToLower();
                query = query.Where(x =>
                    (x.user.Login != null && x.user.Login.ToLower().Contains(search)) ||
                    (x.user.Email != null && x.user.Email.ToLower().Contains(search)) ||
                    x.user.FirstName.ToLower().Contains(search) ||
                    x.user.LastName.ToLower().Contains(search) ||
                    x.user.Id.ToString().ToLower().Contains(search));
            }
            var rows = await query.OrderByDescending(x => x.block.UpdatedAtUtc).Take(take).ToListAsync(ct);
            return Results.Ok(rows.Select(x => new
            {
                user = ToLifecycleUserDto(x.user, x.block),
                x.block.Reason,
                x.block.Note,
                x.block.BlockedByUserId,
                x.block.BlockedAtUtc,
                x.block.UpdatedAtUtc,
                x.block.ExpiresAtUtc,
            }).ToList());
        });

        app.MapPost("/api/internal/account-lifecycle/block", async (
            AccountBlockRequest request,
            IdentityDbContext db,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            if (request.UserId == request.ActorUserId)
                return Results.BadRequest(new { message = "Нельзя заблокировать собственный аккаунт.", code = "CANNOT_BLOCK_SELF" });

            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == request.UserId, ct);
            if (user == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            if (!string.Equals(user.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { message = "Аккаунт уже удалён или объединён.", code = "ACCOUNT_NOT_ACTIVE" });
            if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Администраторский аккаунт нельзя заблокировать через менеджер дублей.", code = "ADMIN_ACCOUNT_PROTECTED" });

            var now = DateTimeOffset.UtcNow;
            var block = await db.BlockedAccounts.FirstOrDefaultAsync(x => x.UserId == request.UserId, ct);
            if (block == null)
            {
                block = new BlockedAccount
                {
                    UserId = request.UserId,
                    BlockedAtUtc = now,
                };
                db.BlockedAccounts.Add(block);
            }
            else if (block.ExpiresAtUtc.HasValue && block.ExpiresAtUtc <= now)
            {
                block.BlockedAtUtc = now;
            }
            block.Reason = CleanLifecycleText(request.Reason, 120) ?? "manual";
            block.Note = CleanLifecycleText(request.Note, 2000);
            block.BlockedByUserId = request.ActorUserId;
            block.UpdatedAtUtc = now;
            block.ExpiresAtUtc = request.ExpiresAtUtc;
            await db.SaveChangesAsync(ct);
            await BlockedAccountCacheSynchronizer.SetBlockedAsync(services, request.UserId, "blocked", ct);
            return Results.Ok(ToLifecycleUserDto(user, block));
        });

        app.MapPost("/api/internal/account-lifecycle/unblock", async (
            AccountUnblockRequest request,
            IdentityDbContext db,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            if (request.UserId == request.ActorUserId)
                return Results.BadRequest(new { message = "Нельзя менять блокировку собственного аккаунта здесь.", code = "CANNOT_UNBLOCK_SELF" });

            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == request.UserId, ct);
            if (user == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            if (!string.Equals(user.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { message = "Удалённый или объединённый аккаунт нельзя разблокировать.", code = "ACCOUNT_NOT_ACTIVE" });

            var block = await db.BlockedAccounts.FirstOrDefaultAsync(x => x.UserId == request.UserId, ct);
            if (block != null)
            {
                db.BlockedAccounts.Remove(block);
                await db.SaveChangesAsync(ct);
            }
            await BlockedAccountCacheSynchronizer.RemoveBlockedAsync(services, request.UserId, ct);
            return Results.Ok(new { userId = request.UserId, blocked = false });
        });

        app.MapPost("/api/internal/account-lifecycle/prepare-merge", async (
            AccountLifecycleRequest request,
            IdentityDbContext db,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            if (!request.TargetUserId.HasValue || request.TargetUserId == Guid.Empty || request.SourceUserId == request.TargetUserId)
                return Results.BadRequest(new { message = "Для объединения нужны два разных аккаунта.", code = "INVALID_MERGE_USERS" });
            if (request.SourceUserId == request.ActorUserId || request.TargetUserId == request.ActorUserId)
                return Results.BadRequest(new { message = "Нельзя объединять собственный администраторский аккаунт.", code = "CANNOT_MERGE_SELF" });

            var users = await db.Users.Where(x => x.Id == request.SourceUserId || x.Id == request.TargetUserId.Value).ToListAsync(ct);
            var source = users.FirstOrDefault(x => x.Id == request.SourceUserId);
            var target = users.FirstOrDefault(x => x.Id == request.TargetUserId.Value);
            if (source == null || target == null) return Results.NotFound(new { message = "Один из аккаунтов не найден.", code = "USER_NOT_FOUND" });
            if (!string.Equals(source.AccountStatus, "active", StringComparison.OrdinalIgnoreCase) || !string.Equals(target.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { message = "Объединять можно только активные аккаунты.", code = "ACCOUNT_NOT_ACTIVE" });
            if (string.Equals(source.Role, "Admin", StringComparison.OrdinalIgnoreCase) || string.Equals(target.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Администраторские аккаунты защищены от объединения.", code = "ADMIN_ACCOUNT_PROTECTED" });

            await UpsertLifecycleBlockAsync(db, source.Id, request.ActorUserId, "merge-in-progress", request.Reason, ct);
            await BlockedAccountCacheSynchronizer.SetBlockedAsync(services, source.Id, "merge-in-progress", ct);
            return Results.Ok(new { source = ToLifecycleUserDto(source, await db.BlockedAccounts.AsNoTracking().FirstAsync(x => x.UserId == source.Id, ct)), target = ToLifecycleUserDto(target, null) });
        });

        app.MapPost("/api/internal/account-lifecycle/prepare-delete", async (
            AccountLifecycleRequest request,
            IdentityDbContext db,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            if (request.SourceUserId == request.ActorUserId)
                return Results.BadRequest(new { message = "Нельзя удалить собственный аккаунт.", code = "CANNOT_DELETE_SELF" });
            var source = await db.Users.FirstOrDefaultAsync(x => x.Id == request.SourceUserId, ct);
            if (source == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            if (!string.Equals(source.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { message = "Аккаунт уже удалён или объединён.", code = "ACCOUNT_NOT_ACTIVE" });
            if (string.Equals(source.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Администраторский аккаунт защищён от удаления.", code = "ADMIN_ACCOUNT_PROTECTED" });

            await UpsertLifecycleBlockAsync(db, source.Id, request.ActorUserId, "deletion-in-progress", request.Reason, ct);
            await BlockedAccountCacheSynchronizer.SetBlockedAsync(services, source.Id, "deletion-in-progress", ct);
            return Results.Ok(ToLifecycleUserDto(source, await db.BlockedAccounts.AsNoTracking().FirstAsync(x => x.UserId == source.Id, ct)));
        });

        app.MapPost("/api/internal/account-lifecycle/finalize-merge", async (
            AccountLifecycleRequest request,
            IdentityDbContext db,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            if (!request.TargetUserId.HasValue || request.TargetUserId == Guid.Empty || request.SourceUserId == request.TargetUserId)
                return Results.BadRequest(new { message = "Некорректная пара аккаунтов.", code = "INVALID_MERGE_USERS" });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var source = await db.Users.FirstOrDefaultAsync(x => x.Id == request.SourceUserId, ct);
            var target = await db.Users.FirstOrDefaultAsync(x => x.Id == request.TargetUserId.Value, ct);
            if (source == null || target == null) return Results.NotFound(new { message = "Один из аккаунтов не найден.", code = "USER_NOT_FOUND" });
            if (string.Equals(source.AccountStatus, "merged", StringComparison.OrdinalIgnoreCase) && source.MergedIntoUserId == target.Id)
                return Results.Ok(new { sourceUserId = source.Id, targetUserId = target.Id, status = "already-merged" });

            var warnings = new List<string>();
            var sourceEmail = source.Email;
            var sourcePhone = source.PhoneNumber;
            var sourcePicture = source.ProfilePictureUrl;
            var sourceAdditionalData = source.AdditionalDataJson;
            var sourceTelegramChatId = source.TelegramChatId;
            var sourceTelegramUsername = source.TelegramUsername;
            var sourceTelegramLinkedAtUtc = source.TelegramLinkedAtUtc;
            var sourceTelegramLinkCount = source.TelegramLinkCount;
            var sourceRole = source.Role;
            var sourceLastLoginAt = source.LastLoginAt;

            var sourceRoles = await db.UserFeatureRoles.Where(x => x.UserId == source.Id).ToListAsync(ct);
            var targetRoleCodes = await db.UserFeatureRoles.Where(x => x.UserId == target.Id).Select(x => x.Code).ToListAsync(ct);
            var targetRoleSet = targetRoleCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var role in sourceRoles)
            {
                if (targetRoleSet.Add(role.Code)) role.UserId = target.Id;
                else db.UserFeatureRoles.Remove(role);
            }

            var loginLogs = await db.LoginLogs.Where(x => x.UserId == source.Id).ToListAsync(ct);
            foreach (var log in loginLogs) log.UserId = target.Id;

            var sourceSettings = await db.UiSettings.FirstOrDefaultAsync(x => x.UserId == source.Id, ct);
            var targetSettings = await db.UiSettings.FirstOrDefaultAsync(x => x.UserId == target.Id, ct);
            if (sourceSettings != null)
            {
                if (targetSettings == null) sourceSettings.UserId = target.Id;
                else db.UiSettings.Remove(sourceSettings);
            }

            var linkCodes = await db.TelegramLinkCodes.Where(x => x.UserId == source.Id).ToListAsync(ct);
            db.TelegramLinkCodes.RemoveRange(linkCodes);

            if (!string.IsNullOrWhiteSpace(sourceEmail) && !string.IsNullOrWhiteSpace(target.Email) &&
                !string.Equals(sourceEmail, target.Email, StringComparison.OrdinalIgnoreCase))
                warnings.Add("У аккаунтов разные email; сохранён email основного аккаунта.");
            if (sourceTelegramChatId.HasValue && target.TelegramChatId.HasValue && target.TelegramChatId != sourceTelegramChatId)
                warnings.Add("У обоих аккаунтов были разные Telegram-привязки; сохранена привязка основного аккаунта.");

            source.AccountStatus = "merged";
            source.MergedIntoUserId = target.Id;
            source.DeletedAtUtc = DateTimeOffset.UtcNow;
            source.DeletedByUserId = request.ActorUserId;
            source.DeletionReason = CleanLifecycleText(request.Reason, 1000) ?? "merged";
            AnonymizeIdentityUser(source, "merged");
            var block = await db.BlockedAccounts.FirstOrDefaultAsync(x => x.UserId == source.Id, ct);
            if (block != null) db.BlockedAccounts.Remove(block);

            // First release unique identity values from the source account. This prevents
            // transient unique-index conflicts when email or Telegram is transferred.
            await db.SaveChangesAsync(ct);

            if (string.IsNullOrWhiteSpace(target.Email) && !string.IsNullOrWhiteSpace(sourceEmail)) target.Email = sourceEmail;
            if (string.IsNullOrWhiteSpace(target.PhoneNumber) && !string.IsNullOrWhiteSpace(sourcePhone)) target.PhoneNumber = sourcePhone;
            if (string.IsNullOrWhiteSpace(target.ProfilePictureUrl) && !string.IsNullOrWhiteSpace(sourcePicture)) target.ProfilePictureUrl = sourcePicture;
            if (string.IsNullOrWhiteSpace(target.AdditionalDataJson) && !string.IsNullOrWhiteSpace(sourceAdditionalData)) target.AdditionalDataJson = sourceAdditionalData;
            if (!target.TelegramChatId.HasValue && sourceTelegramChatId.HasValue)
            {
                target.TelegramChatId = sourceTelegramChatId;
                target.TelegramUsername = sourceTelegramUsername;
                target.TelegramLinkedAtUtc = sourceTelegramLinkedAtUtc;
                target.TelegramLinkCount = Math.Max(target.TelegramLinkCount, sourceTelegramLinkCount);
            }
            if (RoleRank(sourceRole) > RoleRank(target.Role)) target.Role = sourceRole;
            if (sourceLastLoginAt.HasValue && (!target.LastLoginAt.HasValue || sourceLastLoginAt > target.LastLoginAt)) target.LastLoginAt = sourceLastLoginAt;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await BlockedAccountCacheSynchronizer.SetBlockedAsync(services, source.Id, "merged", ct);
            return Results.Ok(new { sourceUserId = source.Id, targetUserId = target.Id, status = "merged", warnings });
        });

        app.MapPost("/api/internal/account-lifecycle/finalize-delete", async (
            AccountLifecycleRequest request,
            IdentityDbContext db,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var source = await db.Users.FirstOrDefaultAsync(x => x.Id == request.SourceUserId, ct);
            if (source == null)
                return Results.Ok(new { sourceUserId = request.SourceUserId, status = "already-deleted" });
            if (string.Equals(source.AccountStatus, "deleted", StringComparison.OrdinalIgnoreCase) && !request.HardDelete)
                return Results.Ok(new { sourceUserId = source.Id, status = "already-deleted" });

            db.UserFeatureRoles.RemoveRange(await db.UserFeatureRoles.Where(x => x.UserId == source.Id).ToListAsync(ct));
            db.LoginLogs.RemoveRange(await db.LoginLogs.Where(x => x.UserId == source.Id).ToListAsync(ct));
            db.TelegramLinkCodes.RemoveRange(await db.TelegramLinkCodes.Where(x => x.UserId == source.Id).ToListAsync(ct));
            var settings = await db.UiSettings.FirstOrDefaultAsync(x => x.UserId == source.Id, ct);
            if (settings != null) db.UiSettings.Remove(settings);
            var block = await db.BlockedAccounts.FirstOrDefaultAsync(x => x.UserId == source.Id, ct);
            if (block != null) db.BlockedAccounts.Remove(block);

            if (request.HardDelete)
            {
                db.Users.Remove(source);
            }
            else
            {
                source.AccountStatus = "deleted";
                source.MergedIntoUserId = null;
                source.DeletedAtUtc = DateTimeOffset.UtcNow;
                source.DeletedByUserId = request.ActorUserId;
                source.DeletionReason = CleanLifecycleText(request.Reason, 1000) ?? "deleted-by-admin";
                AnonymizeIdentityUser(source, "deleted");
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await BlockedAccountCacheSynchronizer.SetBlockedAsync(services, request.SourceUserId, request.HardDelete ? "purged" : "deleted", ct);
            return Results.Ok(new { sourceUserId = request.SourceUserId, status = request.HardDelete ? "purged" : "deleted" });
        });

        return app;
    }

    private static async Task UpsertLifecycleBlockAsync(
        IdentityDbContext db,
        Guid userId,
        Guid actorUserId,
        string reason,
        string? note,
        CancellationToken ct)
    {
        var block = await db.BlockedAccounts.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        var now = DateTimeOffset.UtcNow;
        if (block == null)
        {
            block = new BlockedAccount { UserId = userId, BlockedAtUtc = now };
            db.BlockedAccounts.Add(block);
        }
        block.Reason = reason;
        block.Note = CleanLifecycleText(note, 2000);
        block.BlockedByUserId = actorUserId;
        block.UpdatedAtUtc = now;
        block.ExpiresAtUtc = null;
        await db.SaveChangesAsync(ct);
    }

    private static object ToLifecycleUserDto(IdentityUser user, BlockedAccount? block) => new
    {
        userId = user.Id,
        user.Login,
        user.Email,
        user.FirstName,
        user.LastName,
        displayName = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim(),
        user.Role,
        user.CreatedAt,
        user.LastLoginAt,
        user.AccountStatus,
        user.MergedIntoUserId,
        user.DeletedAtUtc,
        blocked = block != null && (!block.ExpiresAtUtc.HasValue || block.ExpiresAtUtc > DateTimeOffset.UtcNow),
        blockReason = block?.Reason,
        blockNote = block?.Note,
        blockedAtUtc = block?.BlockedAtUtc,
        updatedAtUtc = block?.UpdatedAtUtc,
        expiresAtUtc = block?.ExpiresAtUtc,
    };

    private static void AnonymizeIdentityUser(IdentityUser user, string prefix)
    {
        var token = user.Id.ToString("N");
        user.Login = $"{prefix}_{token}"[..Math.Min(64, prefix.Length + 1 + token.Length)];
        user.Email = null;
        user.FirstName = prefix == "merged" ? "Объединённый" : "Удалённый";
        user.LastName = "аккаунт";
        user.PhoneNumber = null;
        user.ProfilePictureUrl = null;
        user.AdditionalDataJson = null;
        user.TelegramChatId = null;
        user.TelegramUsername = null;
        user.TelegramLinkedAtUtc = null;
        user.TelegramLinkCount = 0;
        user.PasswordSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.PasswordHash = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        user.LastLoginAt = null;
        user.Role = "User";
    }

    private static int RoleRank(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "admin" => 4,
        "editor" or "learningeditor" => 3,
        "teacher" => 2,
        _ => 1,
    };

    private static string? CleanLifecycleText(string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
