using Microsoft.EntityFrameworkCore;
using TaskForge.Identity.Api.Contracts;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;
using static TaskForge.Identity.Api.Services.Access.IdentityApiAccessService;
using static TaskForge.Identity.Api.Services.Common.IdentityApiCommonService;
using static TaskForge.Identity.Api.Services.Mapping.IdentityApiMappingService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

namespace TaskForge.Identity.Api.Endpoints;

internal static partial class IdentityApiEndpoints
{
    private static WebApplication MapFeatureRolesEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/feature-roles", async (HttpContext http, IdentityDbContext db, CancellationToken ct) =>
        {
            var actorRole = await ActorRoleAsync(db, http.User, ct);
            if (actorRole == null)
                return Results.Forbid();

            var roles = await db.FeatureRoles.AsNoTracking().OrderByDescending(x => x.Rank).ThenBy(x => x.Code).ToListAsync(ct);
            var featureAssignments = await db.UserFeatureRoles.AsNoTracking()
                .Select(x => new { x.UserId, x.Code })
                .ToListAsync(ct);
            var baseAssignments = await db.Users.AsNoTracking()
                .Select(x => new { UserId = x.Id, Code = x.Role })
                .ToListAsync(ct);
            var memberCounts = featureAssignments
                .Concat(baseAssignments)
                .Where(x => !string.IsNullOrWhiteSpace(x.Code))
                .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Select(y => y.UserId).Distinct().Count(), StringComparer.OrdinalIgnoreCase);

            return Results.Ok(roles.Select(x => new
            {
                x.Id,
                x.Code,
                title = x.Title,
                name = x.Title,
                x.Description,
                x.Rank,
                x.IsSystem,
                x.IsAssignable,
                x.IsActive,
                membersCount = memberCounts.GetValueOrDefault(x.Code),
                canAssign = !string.Equals(x.Code, "User", StringComparison.OrdinalIgnoreCase) && CanManageRole(actorRole, x),
                canUseAsPrimary = x.IsAssignable && actorRole.Rank > x.Rank,
                canManageDefinition = !x.IsSystem && CanManageRole(actorRole, x)
            }).ToList());
        });

        app.MapGet("/api/admin/feature-roles/users", async (HttpContext http, IdentityDbContext db, string? query, int limit = 50, CancellationToken ct = default) =>
        {
            var actorRole = await ActorRoleAsync(db, http.User, ct);
            if (actorRole == null)
                return Results.Forbid();

            var rows = await SearchUsersAsync(db, query, null, false, "login", "asc", Math.Clamp(limit, 1, 200));
            var ids = rows.Select(x => x.Id).ToHashSet();
            var roleRows = await db.UserFeatureRoles.AsNoTracking().Where(x => ids.Contains(x.UserId)).ToListAsync(ct);
            return Results.Ok(rows.Select(u => ToAdminUserDto(u, roleRows.Where(r => r.UserId == u.Id).Select(r => r.Code).ToArray())).ToList());
        });

        app.MapPost("/api/admin/feature-roles", async (HttpContext http, FeatureRoleRequest request, IdentityDbContext db, CancellationToken ct) =>
        {
            var actorRole = await ActorRoleAsync(db, http.User, ct);
            if (actorRole == null || actorRole.Rank <= CustomRoleRank)
                return Results.Forbid();

            var code = NormalizeRoleCode(request.Code);
            if (string.IsNullOrWhiteSpace(code))
                return Results.BadRequest(new { message = "Код роли обязателен.", code = "ROLE_CODE_REQUIRED" });
            if (await db.FeatureRoles.AnyAsync(x => x.Code == code, ct))
                return Results.Conflict(new { message = "Такая роль уже существует.", code = "ROLE_ALREADY_EXISTS" });

            var role = new FeatureRole
            {
                Code = code,
                Title = string.IsNullOrWhiteSpace(request.Title) ? code : request.Title.Trim(),
                Description = request.Description,
                Rank = CustomRoleRank,
                IsSystem = false,
                IsAssignable = true,
                IsActive = request.IsActive ?? true
            };
            db.FeatureRoles.Add(role);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                role.Id,
                role.Code,
                title = role.Title,
                name = role.Title,
                role.Description,
                role.Rank,
                role.IsSystem,
                role.IsAssignable,
                role.IsActive,
                canAssign = true,
                canManageDefinition = true
            });
        });

        app.MapPut("/api/admin/feature-roles/{id:guid}", async (Guid id, HttpContext http, FeatureRoleRequest request, IdentityDbContext db, CancellationToken ct) =>
        {
            var actorRole = await ActorRoleAsync(db, http.User, ct);
            var role = await db.FeatureRoles.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (role == null)
                return Results.NotFound(new { message = "Роль не найдена.", code = "ROLE_NOT_FOUND" });
            if (role.IsSystem)
                return Results.Conflict(new { message = "Системную роль нельзя переименовать, отключить или изменить через админку.", code = "SYSTEM_ROLE_IMMUTABLE" });
            if (!CanManageRole(actorRole, role))
                return Results.Forbid();

            var nextCode = NormalizeRoleCode(request.Code ?? role.Code);
            if (string.IsNullOrWhiteSpace(nextCode))
                return Results.BadRequest(new { message = "Код роли обязателен.", code = "ROLE_CODE_REQUIRED" });
            if (!string.Equals(role.Code, nextCode, StringComparison.OrdinalIgnoreCase) &&
                await db.FeatureRoles.AnyAsync(x => x.Code == nextCode, ct))
                return Results.Conflict(new { message = "Такая роль уже существует.", code = "ROLE_ALREADY_EXISTS" });

            var oldCode = role.Code;
            role.Code = nextCode;
            role.Title = string.IsNullOrWhiteSpace(request.Title) ? role.Title : request.Title.Trim();
            role.Description = request.Description ?? role.Description;
            role.IsActive = request.IsActive ?? role.IsActive;
            role.UpdatedAt = DateTimeOffset.UtcNow;

            if (!string.Equals(oldCode, role.Code, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var ur in await db.UserFeatureRoles.Where(x => x.Code == oldCode).ToListAsync(ct))
                    ur.Code = role.Code;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                role.Id,
                role.Code,
                title = role.Title,
                name = role.Title,
                role.Description,
                role.Rank,
                role.IsSystem,
                role.IsAssignable,
                role.IsActive
            });
        });

        app.MapDelete("/api/admin/feature-roles/{id:guid}", async (Guid id, HttpContext http, IdentityDbContext db, CancellationToken ct) =>
        {
            var actorRole = await ActorRoleAsync(db, http.User, ct);
            var role = await db.FeatureRoles.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (role == null)
                return Results.NoContent();
            if (role.IsSystem)
                return Results.Conflict(new { message = "Системную роль нельзя удалить.", code = "SYSTEM_ROLE_IMMUTABLE" });
            if (!CanManageRole(actorRole, role))
                return Results.Forbid();

            db.UserFeatureRoles.RemoveRange(await db.UserFeatureRoles.Where(x => x.Code == role.Code).ToListAsync(ct));
            db.FeatureRoles.Remove(role);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapPost("/api/admin/feature-roles/users/{userId:guid}/roles", async (Guid userId, HttpContext http, RoleAssignRequest request, IdentityDbContext db, CancellationToken ct) =>
        {
            var actorRole = await ActorRoleAsync(db, http.User, ct);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null)
                return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });

            var code = NormalizeRoleCode(request.Code);
            if (string.IsNullOrWhiteSpace(code))
                return Results.BadRequest(new { message = "Роль не указана.", code = "ROLE_REQUIRED" });

            var role = await db.FeatureRoles.FirstOrDefaultAsync(x => x.Code == code, ct);
            if (role == null || !role.IsActive)
                return Results.NotFound(new { message = "Роль не найдена или отключена.", code = "ROLE_NOT_AVAILABLE" });
            if (string.Equals(role.Code, "User", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Базовая роль User меняется через профиль пользователя, а не как дополнительная роль.", code = "BASE_ROLE_ASSIGNMENT_REQUIRED" });
            if (!CanManageRole(actorRole, role))
                return Results.Json(new { message = "Можно выдавать только активные роли, стоящие строго ниже вашей.", code = "ROLE_HIERARCHY_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);

            if (code == "Admin")
                user.Role = "Admin";

            if (!await db.UserFeatureRoles.AnyAsync(x => x.UserId == userId && x.Code == code, ct))
                db.UserFeatureRoles.Add(new UserFeatureRole { UserId = userId, Code = code });

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToAdminUserDto(user, await RolesForUser(db, user)));
        });

        app.MapDelete("/api/admin/feature-roles/users/{userId:guid}/roles/{code}", async (Guid userId, string code, HttpContext http, IdentityDbContext db, CancellationToken ct) =>
        {
            var actorRole = await ActorRoleAsync(db, http.User, ct);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null)
                return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });

            var normalized = NormalizeRoleCode(code);
            var role = await db.FeatureRoles.FirstOrDefaultAsync(x => x.Code == normalized, ct);
            if (role == null)
                return Results.NotFound(new { message = "Роль не найдена.", code = "ROLE_NOT_FOUND" });
            if (!CanManageRole(actorRole, role))
                return Results.Json(new { message = "Можно снимать только роли, стоящие строго ниже вашей.", code = "ROLE_HIERARCHY_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);

            db.UserFeatureRoles.RemoveRange(await db.UserFeatureRoles.Where(x => x.UserId == userId && x.Code == normalized).ToListAsync(ct));
            if (string.Equals(user.Role, normalized, StringComparison.OrdinalIgnoreCase))
                user.Role = "User";

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToAdminUserDto(user, await RolesForUser(db, user)));
        });

        return app;
    }
}
