using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;

using TaskForge.Identity.Api.Contracts;
using static TaskForge.Identity.Api.Services.Access.IdentityApiAccessService;
using static TaskForge.Identity.Api.Services.Common.IdentityApiCommonService;
using static TaskForge.Identity.Api.Services.Image.IdentityApiImageService;
using static TaskForge.Identity.Api.Services.Mapping.IdentityApiMappingService;
using static TaskForge.Identity.Api.Services.Results.IdentityApiResultsService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

namespace TaskForge.Identity.Api.Endpoints;

internal static partial class IdentityApiEndpoints
{
    private static WebApplication MapAdminUsersEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/solution-users", async (IdentityDbContext db, string? q, int take = 50) =>
        {
            var rows = await SearchUsersAsync(db, q, null, false, "login", "asc", System.Math.Clamp(take, 1, 200));
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(u => ToAdminUserDto(u)).ToList());
        });

        app.MapGet("/api/admin/users/{userId:guid}", async (Guid userId, IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            var roles = await db.UserFeatureRoles.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.Code).ToListAsync(ct);
            var block = await db.BlockedAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToAdminUserDto(user, roles, block));
        });

        app.MapGet("/api/admin/users", async (
            IdentityDbContext db,
            string? query,
            string? q,
            string? role,
            string? accountType,
            bool linkedOnly = false,
            string? accountStatus = null,
            bool includeInactive = false,
            string? sortBy = "createdAt",
            string? sortDir = "desc",
            int take = 300,
            CancellationToken ct = default) =>
        {
            take = System.Math.Clamp(take, 1, 500);
            if (!string.IsNullOrWhiteSpace(accountType))
            {
                if (!TryNormalizeAccountType(accountType, out var normalizedAccountType))
                {
                    return Microsoft.AspNetCore.Http.Results.BadRequest(new
                    {
                        message = "accountType должен быть human или ai.",
                        code = "INVALID_ACCOUNT_TYPE"
                    });
                }

                accountType = normalizedAccountType;
            }
            var rows = await SearchUsersAsync(db, query ?? q, role, linkedOnly, sortBy, sortDir, take, includeInactive, accountStatus, accountType);
            var total = await CountUsersAsync(db, query ?? q, role, linkedOnly, includeInactive, accountStatus, accountType);
            var ids = rows.Select(x => x.Id).ToArray();
            var blocks = ids.Length == 0
                ? new Dictionary<Guid, BlockedAccount>()
                : await db.BlockedAccounts.AsNoTracking()
                    .Where(x => ids.Contains(x.UserId))
                    .ToDictionaryAsync(x => x.UserId, ct);
            var now = DateTimeOffset.UtcNow;
            var items = rows.Select(user =>
            {
                blocks.TryGetValue(user.Id, out var block);
                return ToAdminUserDto(user, null, block);
            }).ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                items,
                stats = new
                {
                    total,
                    shown = rows.Count,
                    admins = rows.Count(x => string.Equals(x.Role, "Admin", StringComparison.OrdinalIgnoreCase)),
                    aiAccounts = rows.Count(x => string.Equals(x.AccountType, "ai", StringComparison.OrdinalIgnoreCase)),
                    humanAccounts = rows.Count(x => !string.Equals(x.AccountType, "ai", StringComparison.OrdinalIgnoreCase)),
                    telegramLinked = rows.Count(x => x.TelegramChatId.HasValue),
                    blocked = rows.Count(x => blocks.TryGetValue(x.Id, out var block) && (!block.ExpiresAtUtc.HasValue || block.ExpiresAtUtc > now)),
                    active = rows.Count(x => string.Equals(x.AccountStatus, "active", StringComparison.OrdinalIgnoreCase)),
                    merged = rows.Count(x => string.Equals(x.AccountStatus, "merged", StringComparison.OrdinalIgnoreCase)),
                    deleted = rows.Count(x => string.Equals(x.AccountStatus, "deleted", StringComparison.OrdinalIgnoreCase))
                }
            });
        });

        app.MapPut("/api/admin/users/{userId:guid}", async (Guid userId, AdminUserUpdateRequest request, IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            if (!string.Equals(user.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
                return Microsoft.AspNetCore.Http.Results.Conflict(new { message = "Удалённый или объединённый аккаунт нельзя редактировать.", code = "ACCOUNT_NOT_ACTIVE" });
            if (!string.IsNullOrWhiteSpace(request.Login))
            {
                var login = NormalizeLogin(request.Login);
                if (!IsValidLogin(login, out var loginMessage)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = loginMessage });
                if (await db.Users.AnyAsync(x => x.Login == login && x.Id != user.Id, ct)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Логин уже занят" });
                user.Login = login;
            }
            if (request.Email != null)
            {
                var email = NormalizeOptionalEmail(request.Email);
                if (!string.IsNullOrWhiteSpace(request.Email) && string.IsNullOrWhiteSpace(email)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Email указан в неверном формате" });
                if (!string.IsNullOrWhiteSpace(email) && await db.Users.AnyAsync(x => x.Email == email && x.Id != user.Id, ct)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Email уже занят" });
                user.Email = email;
            }
            if (request.FirstName != null) user.FirstName = request.FirstName.Trim();
            if (request.LastName != null) user.LastName = request.LastName.Trim();
            if (request.PhoneNumber != null) user.PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
            if (request.ProfilePictureUrl != null) user.ProfilePictureUrl = string.IsNullOrWhiteSpace(request.ProfilePictureUrl) ? null : request.ProfilePictureUrl.Trim();
            if (!string.IsNullOrWhiteSpace(request.Role)) user.Role = NormalizeRole(request.Role);
            if (request.AccountType != null)
            {
                if (!TryNormalizeAccountType(request.AccountType, out var accountType)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "accountType должен быть human или ai.", code = "INVALID_ACCOUNT_TYPE" });
                user.AccountType = accountType;
            }
            await db.SaveChangesAsync(ct);
            var roles = await db.UserFeatureRoles.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.Code).ToListAsync(ct);
            var block = await db.BlockedAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToAdminUserDto(user, roles, block));
        });

        app.MapDelete("/api/admin/users/{userId:guid}/telegram-link", async (
            Guid userId,
            IdentityDbContext db,
            IDistributedCache cache,
            CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            if (!string.Equals(user.AccountStatus, "active", StringComparison.OrdinalIgnoreCase))
                return Microsoft.AspNetCore.Http.Results.Conflict(new { message = "Интеграции неактивного аккаунта уже недоступны.", code = "ACCOUNT_NOT_ACTIVE" });

            user.TelegramChatId = null;
            user.TelegramUsername = null;
            user.TelegramLinkedAtUtc = null;
            await db.TelegramLinkCodes.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            await cache.RemoveAsync(PasswordRecoveryUserChallengeKey(userId), ct);

            var roles = await db.UserFeatureRoles.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.Code).ToListAsync(ct);
            var block = await db.BlockedAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToAdminUserDto(user, roles, block));
        });

        app.MapDelete("/api/admin/users/{userId:guid}", (Guid userId) =>
            Microsoft.AspNetCore.Http.Results.Conflict(new
            {
                message = "Прямое удаление отключено: используйте безопасную операцию жизненного цикла аккаунта.",
                code = "ACCOUNT_LIFECYCLE_REQUIRED",
                userId
            }));

        return app;
    }
}
