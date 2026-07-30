using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;

using TaskForge.Execution.Api.Contracts;
using static TaskForge.Execution.Api.Services.Mapping.ExecutionApiMappingService;
using static TaskForge.Execution.Api.Services.Results.ExecutionApiResultsService;
using static TaskForge.Execution.Api.Services.Serialization.ExecutionApiSerializationService;

namespace TaskForge.Execution.Api.Endpoints;

internal static partial class ExecutionApiEndpoints
{
    internal static WebApplication MapExecutionApiEndpoints(this WebApplication app)
    {
        MapServiceInfoEndpoints(app);
        MapCompilerEndpoints(app);
        MapInternalExecutionEndpoints(app);
        MapAccountLifecycleInternalEndpoints(app);

        return app;
    }
}
