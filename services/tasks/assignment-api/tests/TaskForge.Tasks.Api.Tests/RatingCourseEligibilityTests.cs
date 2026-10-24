using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using TaskForge.Tasks.Api.Services.Mapping;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class RatingCourseEligibilityTests
{
    private sealed class FixedFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }

    private static async Task<HashSet<Guid>?> CheckAsync(Guid course, HttpStatusCode status, string json)
    {
        using var client = new HttpClient(new FixedResponseHandler(status, json));
        return await AssignmentApiMappingService.LoadRatingCourseIdsAsync(
            new[] { course }, new FixedFactory(client), new ConfigurationBuilder().Build(), CancellationToken.None);
    }

    [Fact]
    public async Task ExistingCourseKeepsItsAssignmentEligible()
    {
        var id = Guid.NewGuid();
        var active = await CheckAsync(id, HttpStatusCode.OK, $"[{{\"courseId\":\"{id}\"}}]");
        Assert.NotNull(active);
        Assert.Contains(id, active);
    }

    [Fact]
    public async Task DeletedCourseMakesAssignmentIneligible()
    {
        var id = Guid.NewGuid();
        var active = await CheckAsync(id, HttpStatusCode.OK, "[]");
        Assert.NotNull(active);
        Assert.Empty(active);
    }

    [Fact]
    public async Task UnavailableEducationIsNotMistakenForDeletedCourse()
    {
        var id = Guid.NewGuid();
        var active = await CheckAsync(id, HttpStatusCode.ServiceUnavailable, "{}");
        Assert.Null(active);
    }
}
