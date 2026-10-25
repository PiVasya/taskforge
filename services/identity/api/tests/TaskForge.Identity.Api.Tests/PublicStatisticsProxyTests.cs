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

    private sealed class StubHandler(HttpStatusCode solutionsStatus, HttpStatusCode tasksStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string?> Paths { get; } = [];
        public List<string?> InternalKeys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath;
            Paths.Add(path);
            InternalKeys.Add(request.Headers.TryGetValues("X-Internal-Key", out var values)
                ? values.SingleOrDefault() : null);
            if (path?.EndsWith("/public-attempt-statistics", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(tasksStatus)
                {
                    Content = JsonContent.Create(new PublicTaskStatisticsDto(7, 5, 1, 1,
                        [new PublicStatisticsDayDto("2026-10-24", 5, 1, 1)],
                        [new PublicResultTypeDto("test", 5, 4, 1, 0),
                         new PublicResultTypeDto("math", 2, 1, 0, 1)]))
                });
            }
            return Task.FromResult(new HttpResponseMessage(solutionsStatus)
            {
                Content = JsonContent.Create(new UserPublicStatisticsDto(
                    470, 12, 34, 20, 8, 10, 2, null,
                    [new PublicStatisticsDayDto("2026-10-24", 2, 1, 0)],
                    [new PublicRatingDayDto("2026-10-24", 1, 50)],
                    [new PublicStatisticsLanguageDto("cpp", 20, 8)],
                    [new PublicStatisticsSourceDto("code", 8)])
                {
                    ResultAttempts = 20, ResultAccepted = 8, ResultFailed = 10, ResultPending = 2,
                    DailyResults = [new PublicStatisticsDayDto("2026-10-24", 2, 1, 0)],
                    ResultTypes = [new PublicResultTypeDto("code", 12, 5, 5, 2),
                                   new PublicResultTypeDto("sql", 7, 3, 4, 0),
                                   new PublicResultTypeDto("image", 1, 0, 1, 0)]
                })
            });
        }
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:SolutionsApi"] = "http://solutions-api:8080",
            ["Services:TasksApi"] = "http://tasks-api:8080",
            ["InternalApi:Key"] = "test-internal-key"
        }).Build();

    [Fact]
    public async Task StatisticsCombineAllKindsFromBothProtectedServices()
    {
        var id = Guid.NewGuid();
        var handler = new StubHandler(HttpStatusCode.OK);
        var result = await IdentityApiCommonService.FetchPublicStatisticsAsync(id, Configuration(),
            new ClientFactory(new HttpClient(handler)), CancellationToken.None);
        Assert.True(result.Available);
        Assert.NotNull(result.Statistics);
        Assert.Equal(470, result.Statistics.Rating);
        Assert.Equal(27, result.Statistics.ResultAttempts);
        Assert.Equal(13, result.Statistics.ResultAccepted);
        Assert.Equal(11, result.Statistics.ResultFailed);
        Assert.Equal(3, result.Statistics.ResultPending);
        Assert.Equal(new[] { "code", "image", "math", "sql", "test" },
            result.Statistics.ResultTypes!.Select(x => x.Kind).ToArray());
        Assert.Single(result.Statistics.DailyResults!);
        Assert.Equal(7, result.Statistics.DailyResults![0].Accepted);
        Assert.Equal($"/api/internal/users/{id:D}/public-statistics", handler.Paths[0]);
        Assert.Equal($"/api/internal/users/{id:D}/public-attempt-statistics", handler.Paths[1]);
        Assert.All(handler.InternalKeys, key => Assert.Equal("test-internal-key", key));
    }

    [Fact]
    public async Task SolutionsFailureDoesNotInventZeroStatistics()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var result = await IdentityApiCommonService.FetchPublicStatisticsAsync(Guid.NewGuid(), Configuration(),
            new ClientFactory(new HttpClient(handler)), CancellationToken.None);
        Assert.False(result.Available);
        Assert.Null(result.Statistics);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task TasksFailureMustNotShowPartialResultsAsOverallSuccess()
    {
        var handler = new StubHandler(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
        var result = await IdentityApiCommonService.FetchPublicStatisticsAsync(Guid.NewGuid(), Configuration(),
            new ClientFactory(new HttpClient(handler)), CancellationToken.None);
        Assert.False(result.Available);
        Assert.Null(result.Statistics);
        Assert.Equal(2, handler.Paths.Count);
    }
}
