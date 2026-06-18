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
    private static WebApplication MapFeatureRolesEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/feature-roles", async (IdentityDbContext db) => Results.Ok(await db.FeatureRoles.AsNoTracking().OrderBy(x => x.Code).Select(x => new { x.Id, x.Code, title = x.Title, x.Description, x.IsActive }).ToListAsync()));

        app.MapGet("/api/admin/feature-roles/users", async (IdentityDbContext db, string? query, int limit = 50) =>
        {
            var rows = await SearchUsersAsync(db, query, null, false, "login", "asc", Math.Clamp(limit, 1, 200));
            var ids = rows.Select(x => x.Id).ToHashSet();
            var roleRows = await db.UserFeatureRoles.AsNoTracking().Where(x => ids.Contains(x.UserId)).ToListAsync();
            return Results.Ok(rows.Select(u => ToAdminUserDto(u, roleRows.Where(r => r.UserId == u.Id).Select(r => r.Code).ToArray())).ToList());
        });

        app.MapPost("/api/admin/feature-roles", async (FeatureRoleRequest request, IdentityDbContext db) =>
        {
            var code = NormalizeRoleCode(request.Code);
            if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest(new { message = "Код роли обязателен.", code = "ROLE_CODE_REQUIRED" });
            if (await db.FeatureRoles.AnyAsync(x => x.Code == code)) return Results.Conflict(new { message = "Такая роль уже существует.", code = "ROLE_ALREADY_EXISTS" });
            var role = new FeatureRole { Code = code, Title = string.IsNullOrWhiteSpace(request.Title) ? code : request.Title.Trim(), Description = request.Description, IsActive = request.IsActive ?? true };
            db.FeatureRoles.Add(role);
            await db.SaveChangesAsync();
            return Results.Ok(new { role.Id, role.Code, title = role.Title, role.Description, role.IsActive });
        });

        app.MapPut("/api/admin/feature-roles/{id:guid}", async (Guid id, FeatureRoleRequest request, IdentityDbContext db) =>
        {
            var role = await db.FeatureRoles.FindAsync(id);
            if (role == null) return Results.NotFound(new { message = "Роль не найдена.", code = "ROLE_NOT_FOUND" });
            var nextCode = NormalizeRoleCode(request.Code ?? role.Code);
            if (!string.Equals(role.Code, nextCode, StringComparison.OrdinalIgnoreCase) && await db.FeatureRoles.AnyAsync(x => x.Code == nextCode)) return Results.Conflict(new { message = "Такая роль уже существует.", code = "ROLE_ALREADY_EXISTS" });
            var oldCode = role.Code;
            role.Code = nextCode;
            role.Title = string.IsNullOrWhiteSpace(request.Title) ? role.Title : request.Title.Trim();
            role.Description = request.Description ?? role.Description;
            role.IsActive = request.IsActive ?? role.IsActive;
            role.UpdatedAt = DateTimeOffset.UtcNow;
            if (!string.Equals(oldCode, role.Code, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var ur in await db.UserFeatureRoles.Where(x => x.Code == oldCode).ToListAsync()) ur.Code = role.Code;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { role.Id, role.Code, title = role.Title, role.Description, role.IsActive });
        });

        app.MapDelete("/api/admin/feature-roles/{id:guid}", async (Guid id, IdentityDbContext db) =>
        {
            var role = await db.FeatureRoles.FindAsync(id);
            if (role == null) return Results.NoContent();
            db.UserFeatureRoles.RemoveRange(await db.UserFeatureRoles.Where(x => x.Code == role.Code).ToListAsync());
            db.FeatureRoles.Remove(role);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/admin/feature-roles/users/{userId:guid}/roles", async (Guid userId, RoleAssignRequest request, IdentityDbContext db) =>
        {
            var user = await db.Users.FindAsync(userId);
            if (user == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            var code = NormalizeRoleCode(request.Code);
            if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest(new { message = "Роль не указана.", code = "ROLE_REQUIRED" });
            if (!await db.FeatureRoles.AnyAsync(x => x.Code == code)) db.FeatureRoles.Add(new FeatureRole { Code = code, Title = code, IsActive = true });
            if (code is "Admin" or "Editor" or "LearningEditor" or "Minecraft") user.Role = code == "Admin" ? "Admin" : user.Role;
            if (!await db.UserFeatureRoles.AnyAsync(x => x.UserId == userId && x.Code == code)) db.UserFeatureRoles.Add(new UserFeatureRole { UserId = userId, Code = code });
            await db.SaveChangesAsync();
            return Results.Ok(ToAdminUserDto(user, await RolesForUser(db, user)));
        });

        app.MapDelete("/api/admin/feature-roles/users/{userId:guid}/roles/{code}", async (Guid userId, string code, IdentityDbContext db) =>
        {
            var user = await db.Users.FindAsync(userId);
            if (user == null) return Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            var normalized = NormalizeRoleCode(code);
            db.UserFeatureRoles.RemoveRange(await db.UserFeatureRoles.Where(x => x.UserId == userId && x.Code == normalized).ToListAsync());
            if (string.Equals(user.Role, normalized, StringComparison.OrdinalIgnoreCase)) user.Role = "User";
            await db.SaveChangesAsync();
            return Results.Ok(ToAdminUserDto(user, await RolesForUser(db, user)));
        });

        return app;
    }
}
