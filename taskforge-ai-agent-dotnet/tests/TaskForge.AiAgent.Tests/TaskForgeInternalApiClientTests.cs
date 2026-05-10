using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Infrastructure;
using TaskForge.AiAgent.Options;
using Xunit;

namespace TaskForge.AiAgent.Tests;

public sealed class TaskForgeInternalApiClientTests
{
    [Fact]
    public async Task RunTestsAsync_UsesInternalKeyHeaderAndBackendTestCasesField()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new CapturingHandler(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"results\":[]}")
            };
        });

        var client = new TaskForgeInternalApiClient(
            new HttpClient(handler),
            Options.Create(new TaskForgeInternalApiOptions
            {
                BaseUrl = "http://taskforge.local",
                ApiKey = "secret",
                RequestTimeoutSeconds = 30
            }),
            new TestLogger<TaskForgeInternalApiClient>());

        await client.RunTestsAsync(new TestRunRequest
        {
            RunId = Guid.NewGuid(),
            WorkerId = "test-worker",
            Language = "cpp",
            Code = "int main(){return 0;}",
            Tests = [new TestCaseSpec { Input = "", ExpectedOutput = "", IsHidden = false }]
        }, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.True(captured!.Headers.TryGetValues("X-Internal-Key", out var values));
        Assert.Equal("secret", Assert.Single(values));
        Assert.False(captured.Headers.Contains("Authorization"));

        using var doc = JsonDocument.Parse(body!);
        Assert.True(doc.RootElement.TryGetProperty("testCases", out var testCases));
        Assert.False(doc.RootElement.TryGetProperty("tests", out _));
        Assert.Equal(JsonValueKind.Array, testCases.ValueKind);
        Assert.Single(testCases.EnumerateArray());
        Assert.True(doc.RootElement.TryGetProperty("runId", out _));
        Assert.True(doc.RootElement.TryGetProperty("workerId", out _));
    }

    [Fact]
    public async Task RunTestsAsync_RejectsEmptyTestListBeforeCallingBackend()
    {
        var handler = new CapturingHandler(_ => Task.FromException<HttpResponseMessage>(new InvalidOperationException("Backend should not be called.")));
        var client = new TaskForgeInternalApiClient(
            new HttpClient(handler),
            Options.Create(new TaskForgeInternalApiOptions { BaseUrl = "http://taskforge.local" }),
            new TestLogger<TaskForgeInternalApiClient>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RunTestsAsync(new TestRunRequest
        {
            Language = "cpp",
            Code = "int main(){return 0;}"
        }, CancellationToken.None));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public CapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _handler(request);
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
