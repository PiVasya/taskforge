using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

using TaskForge.Solutions.Api.Contracts;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Image.SolutionsApiImageService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private static WebApplication MapQuotasEndpoints(WebApplication app)
    {
        app.MapGet("/api/me/quotas", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();
            var isAdmin = IsAdmin(http, cfg);
            var status = await GetQuotaStatus(db, uid.Value, cfg, HasUnlimitedTaskEnergy(http, cfg), isAdmin, IsAiAccount(http, cfg), http.RequestAborted);
            return Microsoft.AspNetCore.Http.Results.Ok(status);
        });

        app.MapGet("/api/quotas", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();
            var isAdmin = IsAdmin(http, cfg);
            return Microsoft.AspNetCore.Http.Results.Ok(await GetQuotaStatus(db, uid.Value, cfg, HasUnlimitedTaskEnergy(http, cfg), isAdmin, IsAiAccount(http, cfg), http.RequestAborted));
        });

        return app;
    }
}
