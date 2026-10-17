using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TaskForge.Execution.Worker.Tests;

public sealed class TaskForgeHttpDiagnosticsRegressionTests
{
    [Fact]
    public async Task DebugHandler_DoesNotConsumeLargeNonReplayableResponse()
    {
        var expected = new string('X', 8 * 1024 * 1024 + 8);
        using var handler = new TaskForgeDebugHttpHandler(
            NullLogger<TaskForgeDebugHttpHandler>.Instance,
            new TaskForgeDebugServiceName("execution-worker"))
        {
            InnerHandler = new StreamingResponseHandler(expected)
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://runner.local/status");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    private sealed class StreamingResponseHandler(string value) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var source = new MemoryStream(Encoding.UTF8.GetBytes(value));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(source)
            });
        }
    }
}
