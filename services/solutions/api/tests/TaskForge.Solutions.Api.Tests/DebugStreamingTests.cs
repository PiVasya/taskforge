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
    public async Task OrdinaryJsonFlushedMidRequest_IsVisibleImmediately()
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
            Assert.True(clientBody.Length > 0, "Explicit flush must deliver bytes without waiting for the endpoint to finish");

            release.TrySetResult(true);
            await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(clientBody.Length > 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", previous);
        }
    }

    [Fact]
    public async Task LargeJson_StreamsBeforeRequestCompletionAndPreservesEveryByte()
    {
        var previous = Environment.GetEnvironmentVariable("TASKFORGE_DEBUG_LOGS");
        Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", "1");
        try
        {
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var app = new ApplicationBuilder(services);
            var written = NewSignal();
            var release = NewSignal();
            var payload = new string('x', 256 * 1024);

            global::TaskForgeDebugDiagnostics.UseTaskForgeDebugRequestLogging(app, "large-json-test");
            app.Run(async context =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(payload);
                await context.Response.Body.FlushAsync();
                written.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            });

            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Path = "/api/large-response";
            var clientBody = new MemoryStream();
            http.Response.Body = clientBody;

            var pipeline = app.Build()(http);
            await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(payload.Length, clientBody.Length);

            release.TrySetResult(true);
            await pipeline.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(payload, System.Text.Encoding.UTF8.GetString(clientBody.ToArray()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", previous);
        }
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
