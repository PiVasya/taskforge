using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;

using TaskForge.Tasks.Api.Contracts;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    internal static WebApplication MapAssignmentApiEndpoints(this WebApplication app)
    {
        MapServiceInfoEndpoints(app);
        MapAssignmentsEndpoints(app);
        MapAssignmentAuthoringMetadataEndpoints(app);
        MapAssignmentRuntimeMetadataEndpoints(app);
        MapCodeAssignmentAuthoringEndpoints(app);
        MapCodeAssignmentRuntimeEndpoints(app);
        MapImageAssignmentAuthoringEndpoints(app);
        MapImageAssignmentRuntimeEndpoints(app);
        MapTestAssignmentAuthoringEndpoints(app);
        MapTestAssignmentRuntimeEndpoints(app);
        MapMathAssignmentAuthoringEndpoints(app);
        MapMathAssignmentRuntimeEndpoints(app);
        MapSqlAssignmentAuthoringEndpoints(app);
        MapSqlAssignmentRuntimeEndpoints(app);
        MapInternalEndpoints(app);
        MapAgentInvestigationInternalEndpoints(app);
        MapAccountIntelligenceInternalEndpoints(app);
        MapAccountLifecycleInternalEndpoints(app);
        MapTaskTestsEndpoints(app);
        MapMathTasksEndpoints(app);
        MapInsightsEndpoints(app);
        MapAssignmentActivityEndpoints(app);
        MapSqlEndpoints(app);

        return app;
    }
}
