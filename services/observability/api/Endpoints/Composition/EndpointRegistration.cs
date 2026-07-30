using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;

using TaskForge.Observability.Api.Contracts;
using static TaskForge.Observability.Api.Services.Common.ObservabilityApiCommonService;
using static TaskForge.Observability.Api.Services.Mapping.ObservabilityApiMappingService;
using static TaskForge.Observability.Api.Services.Serialization.ObservabilityApiSerializationService;

namespace TaskForge.Observability.Api.Endpoints;

internal static partial class ObservabilityApiEndpoints
{
    internal static WebApplication MapObservabilityApiEndpoints(this WebApplication app)
    {
        MapServiceInfoEndpoints(app);
        MapActivityEndpoints(app);
        MapAccountIntelligenceInternalEndpoints(app);
        MapAccountLifecycleInternalEndpoints(app);
        MapSystemStatusEndpoints(app);
        MapAnalyticsEndpoints(app);

        return app;
    }
}
