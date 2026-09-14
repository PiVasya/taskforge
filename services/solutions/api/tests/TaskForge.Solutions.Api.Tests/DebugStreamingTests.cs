using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class DebugStreamingTests
{
    [Fact]
    public async Task SolutionSse_IsVisibleBeforeRequestCompletes()
    {
        var previous = Environment.GetEnvironmentVariable("TASKFORGE_DEBUG_LOGS");
        Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", "1");
        try
        {
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var app = new ApplicationBuilder(services);
            var frameWritten = NewSignal();
            var release = NewSignal();

            global::TaskForgeDebugDiagnostics.UseTaskForgeDebugRequestLogging(app, "solutions-api");
            app.Run(async context =>
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("event: solution\ndata: {\"id\":1}\n\n");
                await context.Response.Body.FlushAsync();
                frameWritten.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            });

            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Path = "/api/admin/solution-events";
            http.Request.Headers.Accept = "text/event-stream";
            var clientBody = new MemoryStream();
            http.Response.Body = clientBody;

            var pipelineTask = app.Build()(http);
            await frameWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(clientBody.Length > 0, "SSE frame was buffered by debug diagnostics");

            release.TrySetResult(true);
            await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", previous);
        }
    }

    [Fact]
    public async Task OrdinaryJson_IsStillCapturedUntilRequestCompletes()
    {
        var previous = Environment.GetEnvironmentVariable("TASKFORGE_DEBUG_LOGS");
        Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", "1");
        try
        {
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var app = new ApplicationBuilder(services);
            var frameWritten = NewSignal();
            var release = NewSignal();

            global::TaskForgeDebugDiagnostics.UseTaskForgeDebugRequestLogging(app, "solutions-api");
            app.Run(async context =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"ok\":true}");
                await context.Response.Body.FlushAsync();
                frameWritten.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            });

            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Path = "/api/me/solutions";
            http.Request.Headers.Accept = "application/json";
            var clientBody = new MemoryStream();
            http.Response.Body = clientBody;

            var pipelineTask = app.Build()(http);
            await frameWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, clientBody.Length);

            release.TrySetResult(true);
            await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(clientBody.Length > 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", previous);
        }
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
