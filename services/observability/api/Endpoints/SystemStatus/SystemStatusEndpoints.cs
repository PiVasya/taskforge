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
    private static WebApplication MapSystemStatusEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/system-status", () => Results.Ok(new { status = "ok", services = new[] { "identity", "education", "content", "tasks", "quiz", "solutions", "execution", "ai", "support", "minecraft", "files", "notifications", "observability" }, generatedAt = DateTimeOffset.UtcNow }));

        app.MapGet("/api/system-status", () => Results.Ok(new { status = "ok", generatedAt = DateTimeOffset.UtcNow }));

        return app;
    }
}
