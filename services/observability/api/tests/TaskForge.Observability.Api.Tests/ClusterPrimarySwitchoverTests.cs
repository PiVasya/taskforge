using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaskForge.Observability.Api.Services.Cluster;
using Xunit;

namespace TaskForge.Observability.Api.Tests;

public sealed class ClusterPrimarySwitchoverTests
{
    [Fact]
    public async Task Patroni_switchover_has_explicit_content_length_and_preserves_authentication()
    {
        var handler = new PatroniHandler();
        using var service = Service(handler);

        await SendAsync(service);

        var request = Assert.IsType<ObservedPatroniRequest>(handler.Observed);
        Assert.Equal("POST", request.Method);
        Assert.Equal("http://postgres:8008/switchover", request.Uri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal((long)request.Body.Length, request.ContentLength);
        Assert.NotNull(request.ContentLength);
        Assert.NotEqual(true, request.Chunked);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("B", json.RootElement.GetProperty("leader").GetString());
        Assert.Equal("A", json.RootElement.GetProperty("candidate").GetString());
        var digest = HMACSHA512.HashData(Encoding.UTF8.GetBytes("integration-test-key"), Encoding.UTF8.GetBytes("taskforge-patroni-rest-v2"));
        var password = Convert.ToHexString(digest).ToLowerInvariant()[..64];
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"taskforge_patroni:{password}"));
        Assert.Equal($"Basic {expected}", request.Authorization);
    }

    [Fact]
    public async Task Patroni_client_rejection_is_a_confirmed_structured_error()
    {
        var handler = new PatroniHandler(HttpStatusCode.Conflict, """{"error":"target is not available"}""");
        using var service = Service(handler);

        var error = await Assert.ThrowsAsync<ClusterPrimarySwitchException>(() => SendAsync(service));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("PATRONI_SWITCH_REJECTED", error.Code);
        Assert.Contains("HTTP 409", error.Message);
        Assert.Contains("target is not available", error.Message);
    }

    [Fact]
    public async Task Patroni_length_required_returns_an_actionable_confirmed_error()
    {
        var handler = new PatroniHandler(HttpStatusCode.LengthRequired, "<html>server response</html>");
        using var service = Service(handler);

        var error = await Assert.ThrowsAsync<ClusterPrimarySwitchException>(() => SendAsync(service));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("PATRONI_SWITCH_REJECTED", error.Code);
        Assert.Contains("HTTP 411", error.Message);
        Assert.Contains("Content-Length", error.Message);
        Assert.DoesNotContain("html", error.Message);
    }

    [Fact]
    public async Task Patroni_server_failure_remains_uncertain_to_avoid_blind_retry()
    {
        var handler = new PatroniHandler(HttpStatusCode.InternalServerError, "Patroni failed");
        using var service = Service(handler);

        var error = await Assert.ThrowsAsync<ClusterPrimarySwitchException>(() => SendAsync(service));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("PATRONI_SWITCH_FAILED", error.Code);
        Assert.Contains("фактическую Primary", error.Message);
    }

    private static ClusterTelemetryService Service(HttpMessageHandler handler)
        => new(new PatroniClientFactory(handler), new ConfigurationBuilder().Build(),
            NullLogger<ClusterTelemetryService>.Instance, null!);

    private static async Task SendAsync(ClusterTelemetryService service)
    {
        var method = typeof(ClusterTelemetryService).GetMethod("PostPatroniSwitchoverAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(service, ["postgres", 8008, "B", "A", "integration-test-key", CancellationToken.None])!;
        await task;
    }

    private sealed class PatroniClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record ObservedPatroniRequest(string Method, string Uri, string ContentType, long? ContentLength, bool? Chunked, string? Authorization, byte[] Body);

    private sealed class PatroniHandler(HttpStatusCode status = HttpStatusCode.Accepted, string body = "") : HttpMessageHandler
    {
        public ObservedPatroniRequest? Observed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Observed = new ObservedPatroniRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Content.Headers.ContentType?.MediaType ?? string.Empty,
                request.Content.Headers.ContentLength,
                request.Headers.TransferEncodingChunked,
                request.Headers.Authorization?.ToString(),
                bytes);
            var accepted = request.Content.Headers.ContentLength == bytes.Length && request.Headers.TransferEncodingChunked != true;
            return new HttpResponseMessage(accepted ? status : HttpStatusCode.LengthRequired)
            {
                Content = new StringContent(accepted ? body : "Content-Length header required"),
            };
        }
    }
}
