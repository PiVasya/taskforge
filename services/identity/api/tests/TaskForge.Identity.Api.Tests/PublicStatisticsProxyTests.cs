using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using TaskForge.Identity.Api.Contracts;
using TaskForge.Identity.Api.Services.Common;
using Xunit;

namespace TaskForge.Identity.Api.Tests;

public sealed class PublicStatisticsProxyTests
{
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? InternalKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            InternalKey = request.Headers.TryGetValues("X-Internal-Key", out var values)
                ? values.SingleOrDefault() : null;
            var response = new HttpResponseMessage(status)
            {
                Content = JsonContent.Create(new UserPublicStatisticsDto(
                    470, 12, 34, 20, 8, 10, 2, null,
                    [new PublicStatisticsDayDto("2026-10-24", 2, 1, 0)],
                    [new PublicRatingDayDto("2026-10-24", 1, 50)],
                    [new PublicStatisticsLanguageDto("cpp", 20, 8)],
                    [new PublicStatisticsSourceDto("code", 8)]))
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task PublicStatisticsIsLoadedFromProtectedSolutionsEndpoint()
    {
        var id = Guid.NewGuid();
        var handler = new StubHandler(HttpStatusCode.OK);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:SolutionsApi"] = "http://solutions-api:8080",
            ["InternalApi:Key"] = "test-internal-key"
        }).Build();
        var result = await IdentityApiCommonService.FetchPublicStatisticsAsync(id, config,
            new ClientFactory(new HttpClient(handler)), CancellationToken.None);
        Assert.True(result.Available);
        Assert.Equal(470, result.Statistics!.Rating);
        Assert.Equal($"/api/internal/users/{id:D}/public-statistics", handler.LastPath);
        Assert.Equal("test-internal-key", handler.InternalKey);
    }

    [Fact]
    public async Task ServiceFailureDoesNotInventZeroStatistics()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:SolutionsApi"] = "http://solutions-api:8080"
        }).Build();
        var result = await IdentityApiCommonService.FetchPublicStatisticsAsync(Guid.NewGuid(), config,
            new ClientFactory(new HttpClient(handler)), CancellationToken.None);
        Assert.False(result.Available);
        Assert.Null(result.Statistics);
    }
}
