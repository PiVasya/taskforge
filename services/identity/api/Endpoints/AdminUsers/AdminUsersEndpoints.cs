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

        app.MapGet("/api/admin/users", async (
            IdentityDbContext db,
            string? query,
            string? q,
            string? role,
            bool linkedOnly = false,
            string? sortBy = "createdAt",
            string? sortDir = "desc",
            int take = 300) =>
        {
            var rows = await SearchUsersAsync(db, query ?? q, role, linkedOnly, sortBy, sortDir, System.Math.Clamp(take, 1, 500));
            var total = await CountUsersAsync(db, query ?? q, role, linkedOnly);
            var linked = 0;
            var admins = rows.Count(x => string.Equals(x.Role, "Admin", StringComparison.OrdinalIgnoreCase));
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                items = rows.Select(u => ToAdminUserDto(u)).ToList(),
                stats = new { total, linked, admins }
            });
        });

        app.MapPut("/api/admin/users/{userId:guid}", async (Guid userId, AdminUserUpdateRequest request, IdentityDbContext db) =>
        {
            var user = await db.Users.FindAsync(userId);
            if (user == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            if (!string.IsNullOrWhiteSpace(request.Login))
            {
                var login = NormalizeLogin(request.Login);
                if (!IsValidLogin(login, out var loginMessage)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = loginMessage });
                if (await db.Users.AnyAsync(x => x.Login == login && x.Id != user.Id)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Логин уже занят" });
                user.Login = login;
            }
            if (request.Email != null)
            {
                var email = NormalizeOptionalEmail(request.Email);
                if (!string.IsNullOrWhiteSpace(request.Email) && string.IsNullOrWhiteSpace(email)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Email указан в неверном формате" });
                if (!string.IsNullOrWhiteSpace(email) && await db.Users.AnyAsync(x => x.Email == email && x.Id != user.Id)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Email уже занят" });
                user.Email = email;
            }
            if (request.FirstName != null) user.FirstName = request.FirstName.Trim();
            if (request.LastName != null) user.LastName = request.LastName.Trim();
            if (request.PhoneNumber != null) user.PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
            if (request.ProfilePictureUrl != null) user.ProfilePictureUrl = string.IsNullOrWhiteSpace(request.ProfilePictureUrl) ? null : request.ProfilePictureUrl.Trim();
            if (!string.IsNullOrWhiteSpace(request.Role)) user.Role = NormalizeRole(request.Role);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToAdminUserDto(user));
        });

        app.MapDelete("/api/admin/users/{userId:guid}", async (Guid userId, IdentityDbContext db) =>
        {
            var user = await db.Users.FindAsync(userId);
            if (user == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Пользователь не найден.", code = "USER_NOT_FOUND" });
            db.Users.Remove(user);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(new { message = "Пользователь удалён", deleted = true });
        });

        return app;
    }
}
