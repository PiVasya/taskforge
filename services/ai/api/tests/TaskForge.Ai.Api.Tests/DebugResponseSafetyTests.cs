using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskForge.Ai.Api.Tests;

public sealed class DebugResponseSafetyTests
{
    [Fact]
    public async Task HealthJsonResponse_DoesNotCrashOrCorruptTheHealthcheck()
    {
        await RunPipelineAsync("/health/ready", async response =>
        {
            await response.WriteAsJsonAsync(new { status = "ready", service = "ai-api" });
        }, async body =>
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task NormalJsonResponse_UsesBoundedDebugCaptureWithoutRecursiveWrites()
    {
        await RunPipelineAsync("/api/diagnostic-probe", async response =>
        {
            await response.WriteAsJsonAsync(new { status = "ready", service = "ai-api" });
        }, async body =>
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MixedWriteOverloadsAndOverflow_AreDeliveredOnceAndInOrder()
    {
        var original = Encoding.UTF8.GetBytes(new string('x', 128 * 1024));
        await RunPipelineAsync("/api/bounded-response", async response =>
        {
            response.Body.Write(original, 0, 20);
            response.Body.Write(original.AsSpan(20, 20));
            await response.Body.WriteAsync(original.AsMemory(40, 64 * 1024));
            await response.Body.WriteAsync(original.AsMemory(40 + 64 * 1024));
            await response.Body.FlushAsync();
            response.Body.WriteByte((byte)'!');
        }, async body =>
        {
            var bytes = body.ToArray();
            Assert.Equal(original.Length + 1, bytes.Length);
            Assert.True(bytes.AsSpan(0, original.Length).SequenceEqual(original));
            Assert.Equal((byte)'!', bytes[^1]);
            await Task.CompletedTask;
        });
    }

    private static async Task RunPipelineAsync(
        string path,
        Func<HttpResponse, Task> handler,
        Func<MemoryStream, Task> verify)
    {
        var previous = Environment.GetEnvironmentVariable("TASKFORGE_DEBUG_LOGS");
        Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", "1");
        try
        {
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var app = new ApplicationBuilder(services);
            global::TaskForgeDebugDiagnostics.UseTaskForgeDebugRequestLogging(app, "ai-api");
            app.Run(context => handler(context.Response));

            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Path = path;
            using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;
            await app.Build()(context).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            await verify(responseBody);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", previous);
        }
    }
}
