using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;

using TaskForge.Ai.Api.Contracts;
using TaskForge.Ai.Api.Hubs;
using static TaskForge.Ai.Api.Services.Access.AiApiAccessService;
using static TaskForge.Ai.Api.Services.Common.AiApiCommonService;
using static TaskForge.Ai.Api.Services.Mapping.AiApiMappingService;
using static TaskForge.Ai.Api.Services.Results.AiApiResultsService;
using static TaskForge.Ai.Api.Services.Serialization.AiApiSerializationService;
using static TaskForge.Ai.Api.Services.Testing.AiApiTestingService;

namespace TaskForge.Ai.Api.Endpoints;

internal static partial class AiApiEndpoints
{
    internal static WebApplication MapAiApiEndpoints(this WebApplication app)
    {
        MapServiceInfoEndpoints(app);
        MapAgentEndpoints(app);
        MapAccountIntelligenceEndpoints(app);
        MapRealtimeEndpoints(app);
        MapWorkerInternalEndpoints(app);

        return app;
    }
}
