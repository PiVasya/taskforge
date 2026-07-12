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
    private static WebApplication MapInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/users/summaries", async (UserIdsRequest request, IdentityDbContext db, IDistributedCache cache, IConfiguration cfg, ILogger<Program> logger, CancellationToken ct) =>
        {
            var ids = (request.UserIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().Take(1000).OrderBy(x => x).ToArray();
            if (ids.Length == 0)
            {
                TaskForgeDebugTrace.UserSummaryServed("identity-api", ids, Array.Empty<UserSummaryDto>());
                return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<UserSummaryDto>());
            }

            var key = TaskForgeCache.Key("identity:user-summaries:v3", ids);
            var ttl = TaskForgeCache.Ttl(cfg, "UserSummaries", 120);
            var result = await TaskForgeCache.GetOrSetAsync(cache, cfg, logger, key, ttl, async token =>
            {
                TaskForgeDebugTrace.UserSummaryRequest("identity-api", "identity-db", ids);
                var rows = await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(token);
                return rows.Select(ToUserSummaryDto).ToList();
            }, ct);

            TaskForgeDebugTrace.UserSummaryServed("identity-api", ids, result);
            return Microsoft.AspNetCore.Http.Results.Ok(result);
        });


        app.MapPost("/api/internal/feature-roles/users/{userId:guid}/roles", async (Guid userId, RoleAssignRequest request, IdentityDbContext db, CancellationToken ct) =>
        {
            var code = NormalizeRoleCode(request.Code);
            if (string.IsNullOrWhiteSpace(code)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Role code is required." });
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "User not found." });
            if (!await db.FeatureRoles.AnyAsync(x => x.Code == code, ct)) db.FeatureRoles.Add(new FeatureRole { Code = code, Title = code, IsActive = true });
            if (!await db.UserFeatureRoles.AnyAsync(x => x.UserId == userId && x.Code == code, ct)) db.UserFeatureRoles.Add(new UserFeatureRole { UserId = userId, Code = code });
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { userId, code });
        });

        app.MapDelete("/api/internal/feature-roles/users/{userId:guid}/roles/{code}", async (Guid userId, string code, IdentityDbContext db, CancellationToken ct) =>
        {
            var normalized = NormalizeRoleCode(code);
            if (string.IsNullOrWhiteSpace(normalized)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Role code is required." });
            var rows = await db.UserFeatureRoles.Where(x => x.UserId == userId && x.Code == normalized).ToListAsync(ct);
            if (rows.Count > 0) db.UserFeatureRoles.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { userId, code = normalized, removed = rows.Count });
        });

        return app;
    }
}
