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
    private static WebApplication MapCompilerEndpoints(WebApplication app)
    {
        app.MapPost("/api/compiler/compile-run", async (RunnerRequest request, IHttpClientFactory factory, IConfiguration cfg) => await ProxyRunAsync(request, factory, cfg, tests: false));

        app.MapPost("/api/compiler/run-tests", async (RunnerRequest request, IHttpClientFactory factory, IConfiguration cfg) => await ProxyRunAsync(request, factory, cfg, tests: true));

        return app;
    }
}
