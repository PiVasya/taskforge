using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskForge.Browser.Api.Services;

namespace TaskForge.Browser.Api.Diagnostics;

public sealed class BrowserRuntimeHealthCheck(BrowserRuntime runtime) : IHealthCheck
{
    private readonly BrowserRuntime _runtime = runtime;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(_runtime.IsReady
            ? HealthCheckResult.Healthy("Chromium is ready.")
            : HealthCheckResult.Unhealthy("Chromium is not connected."));
}
