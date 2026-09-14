using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class DebugStreamingTests
{
    [Fact]
    public async Task LearningMapNdjson_IsVisibleBeforeRequestCompletes()
    {
        await AssertStreamsImmediatelyAsync(
            "/api/courses/00000000-0000-0000-0000-000000000001/learning-map/stream",
            "application/x-ndjson",
            "{\"type\":\"delta\"}\n");
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

            global::TaskForgeDebugDiagnostics.UseTaskForgeDebugRequestLogging(app, "tasks-api");
            app.Run(async context =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"ok\":true}");
                await context.Response.Body.FlushAsync();
                frameWritten.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            });

            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Path = "/api/assignments/1";
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

    private static async Task AssertStreamsImmediatelyAsync(string path, string contentType, string frame)
    {
        var previous = Environment.GetEnvironmentVariable("TASKFORGE_DEBUG_LOGS");
        Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", "1");
        try
        {
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var app = new ApplicationBuilder(services);
            var frameWritten = NewSignal();
            var release = NewSignal();

            global::TaskForgeDebugDiagnostics.UseTaskForgeDebugRequestLogging(app, "tasks-api");
            app.Run(async context =>
            {
                context.Response.ContentType = contentType;
                await context.Response.WriteAsync(frame);
                await context.Response.Body.FlushAsync();
                frameWritten.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            });

            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Path = path;
            http.Request.Headers.Accept = contentType;
            var clientBody = new MemoryStream();
            http.Response.Body = clientBody;

            var pipelineTask = app.Build()(http);
            await frameWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(clientBody.Length > 0, "streaming frame was buffered by debug diagnostics");

            release.TrySetResult(true);
            await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", previous);
        }
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
