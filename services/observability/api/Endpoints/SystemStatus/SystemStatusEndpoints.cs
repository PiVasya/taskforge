using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;
using TaskForge.Observability.Api.Services.Cluster;

using TaskForge.Observability.Api.Contracts;
using static TaskForge.Observability.Api.Services.Common.ObservabilityApiCommonService;
using static TaskForge.Observability.Api.Services.Mapping.ObservabilityApiMappingService;
using static TaskForge.Observability.Api.Services.Serialization.ObservabilityApiSerializationService;

namespace TaskForge.Observability.Api.Endpoints;

internal static partial class ObservabilityApiEndpoints
{
    private static WebApplication MapSystemStatusEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/system-status", (ClusterTelemetryService telemetry, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";
            return Microsoft.AspNetCore.Http.Results.Ok(telemetry.BuildPublicSnapshot());
        });

        app.MapGet("/api/admin/cluster", (ClusterTelemetryService telemetry, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";
            return Microsoft.AspNetCore.Http.Results.Ok(telemetry.BuildPublicSnapshot());
        });

        app.MapPost("/api/admin/cluster/primary", async (
            ClusterPrimarySwitchRequest request,
            ClusterTelemetryService telemetry,
            HttpResponse response,
            CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            try
            {
                var result = await telemetry.RequestPrimarySwitchAsync(request.Target, ct);
                return Results.Json(result, statusCode: result.Changed ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
            }
            catch (ClusterPrimarySwitchException ex)
            {
                return Results.Json(new
                {
                    status = ex.StatusCode,
                    code = ex.Code,
                    message = ex.Message,
                    severity = ex.StatusCode >= 500 ? "error" : "warning"
                }, statusCode: ex.StatusCode);
            }
        });

        app.MapGet("/api/system-status", () =>
            Microsoft.AspNetCore.Http.Results.Ok(new { status = "ok", generatedAt = DateTimeOffset.UtcNow }));

        return app;
    }
}

internal sealed record ClusterPrimarySwitchRequest(string? Target);
