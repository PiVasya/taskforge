using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;
using TaskForge.Support.Api.Domain;

using TaskForge.Support.Api.Contracts;
using TaskForge.Support.Api.Hubs;
using static TaskForge.Support.Api.Services.Common.SupportApiCommonService;
using static TaskForge.Support.Api.Services.Mapping.SupportApiMappingService;
using static TaskForge.Support.Api.Services.Serialization.SupportApiSerializationService;

namespace TaskForge.Support.Api.Endpoints;

internal static partial class SupportApiEndpoints
{
    internal static WebApplication MapSupportApiEndpoints(this WebApplication app)
    {
        MapServiceInfoEndpoints(app);
        MapTicketsEndpoints(app);
        MapIntegrationsEndpoints(app);
        MapRealtimeEndpoints(app);
        MapAccountLifecycleInternalEndpoints(app);

        return app;
    }
}
