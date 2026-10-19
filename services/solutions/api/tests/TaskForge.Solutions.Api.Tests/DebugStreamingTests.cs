using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using TaskForge.Solutions.Api.Services.Mapping;
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

    [Theory]
    [InlineData("/api/me/solutions")]
    [InlineData("/api/admin/users/00000000-0000-0000-0000-000000000001/solutions")]
    [InlineData("/api/code-solutions/top")]
    public async Task StreamedSolutionJson_UsesOnlyAsyncIoWithDebugLogging(string path)
    {
        var previous = Environment.GetEnvironmentVariable("TASKFORGE_DEBUG_LOGS");
        Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", "1");
        try
        {
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var app = new ApplicationBuilder(services);
            var firstItemFlushed = NewSignal();
            var release = NewSignal();

            global::TaskForgeDebugDiagnostics.UseTaskForgeDebugRequestLogging(app, "solutions-api");
            app.Run(async context =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await SolutionsApiMappingService.WriteStreamingJsonArrayAsync(context.Response.Body, async (writer, cancellationToken) =>
                {
                    JsonSerializer.Serialize(writer, new { id = "first" });
                    await writer.FlushAsync(cancellationToken);
                    firstItemFlushed.TrySetResult(true);
                    // The test controls completion: do not race a 5-second timer
                    // against a loaded CI runner after the first flush.
                    await release.Task;
                    JsonSerializer.Serialize(writer, new { id = "second" });
                }, context.RequestAborted);
            });

            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Path = path;
            http.Request.Headers.Accept = "application/json";
            // Kestrel disables synchronous response writes/flushes by default.
            // A MemoryStream alone would hide the old Utf8JsonWriter.Dispose() crash.
            var clientBody = new AsyncOnlyResponseStream();
            http.Response.Body = clientBody;

            var pipelineTask = app.Build()(http);
            try
            {
                // If middleware or the writer throws before the first flush, surface
                // the original pipeline exception instead of hiding it behind a timeout.
                await Task.WhenAny(firstItemFlushed.Task, pipelineTask).WaitAsync(TimeSpan.FromSeconds(5));
                if (pipelineTask.IsCompleted)
                {
                    await pipelineTask;
                    Assert.Fail("The HTTP pipeline completed before streaming the first item");
                }
                await firstItemFlushed.Task;
                var firstFrame = Encoding.UTF8.GetString(clientBody.GetWrittenBytes());
                Assert.StartsWith("[", firstFrame);
                Assert.Contains("first", firstFrame);
                Assert.False(pipelineTask.IsCompleted, "Streaming must send the first item before all batches complete");

                release.TrySetResult(true);
                await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5));
                using var result = JsonDocument.Parse(clientBody.GetWrittenBytes());
                Assert.Equal(JsonValueKind.Array, result.RootElement.ValueKind);
                Assert.Equal(2, result.RootElement.GetArrayLength());
                Assert.Equal("first", result.RootElement[0].GetProperty("id").GetString());
                Assert.Equal("second", result.RootElement[1].GetProperty("id").GetString());
            }
            finally
            {
                // Never leave a request stuck on the test's gate after an assertion.
                release.TrySetResult(true);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", previous);
        }
    }

    [Fact]
    public async Task DebugCapture_ForOrdinaryJson_DoesNotRequireSynchronousIo()
    {
        var previous = Environment.GetEnvironmentVariable("TASKFORGE_DEBUG_LOGS");
        Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", "1");
        try
        {
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var app = new ApplicationBuilder(services);
            global::TaskForgeDebugDiagnostics.UseTaskForgeDebugRequestLogging(app, "solutions-api");
            app.Run(async context =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true}"), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            });

            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Path = "/api/ordinary-json-test";
            http.Request.Headers.Accept = "application/json";
            var clientBody = new AsyncOnlyResponseStream();
            http.Response.Body = clientBody;

            await app.Build()(http).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(clientBody.GetWrittenBytes()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASKFORGE_DEBUG_LOGS", previous);
        }
    }

    [Fact]
    public async Task EmptyStreamedSolutionJson_DoesNotSynchronouslyFlushOnDispose()
    {
        var clientBody = new AsyncOnlyResponseStream();
        await SolutionsApiMappingService.WriteStreamingJsonArrayAsync(
            clientBody, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("[]", Encoding.UTF8.GetString(clientBody.GetWrittenBytes()));
    }

    private sealed class AsyncOnlyResponseStream : Stream
    {
        private readonly MemoryStream _inner = new();
        internal byte[] GetWrittenBytes() => _inner.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed");
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("Synchronous operations are disallowed");
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
