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
    internal static WebApplication MapSolutionsApiEndpoints(this WebApplication app)
    {
        MapServiceInfoEndpoints(app);
        MapSubmissionsEndpoints(app);
        MapInternalEndpoints(app);
        MapAccountIntelligenceInternalEndpoints(app);
        MapLeaderboardEndpoints(app);
        MapQuotasEndpoints(app);
        MapBadgesEndpoints(app);
        MapImageSolutionsEndpoints(app);

        return app;
    }
}
