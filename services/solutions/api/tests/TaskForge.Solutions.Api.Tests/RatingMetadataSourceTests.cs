using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TaskForge.Solutions.Api.Services.Mapping;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class RatingMetadataSourceTests
{
    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CatalogHandler(Guid courseId, bool courseExists, bool courseUnavailable = false) : HttpMessageHandler
    {
        public int AssignmentBatches { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/internal/assignments/summaries")
            {
                AssignmentBatches++;
                using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var ids = input.RootElement.GetProperty("assignmentIds").EnumerateArray().Select(x => x.GetGuid());
                var rows = ids.Select(id => new { id, assignmentId = id, courseId, title = "Existing task", rating = 9 });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(rows) };
            }
            if (request.RequestUri?.AbsolutePath == "/api/internal/courses/metadata")
            {
                if (courseUnavailable)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(courseExists
                        ? (object)new[] { new { id = courseId, courseId, title = "Existing course" } }
                        : Array.Empty<object>())
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static async Task<Dictionary<Guid, TaskForge.Solutions.Api.Contracts.AssignmentMetadata>> LoadAsync(
        Guid[] taskIds, CatalogHandler handler)
    {
        using var client = new HttpClient(handler);
        return await SolutionsApiMappingService.LoadAssignmentMetadataAsync(
            taskIds, new ConfigurationBuilder().Build(), new SingleClientFactory(client),
            CancellationToken.None, requireRatingSources: true);
    }

    [Fact]
    public async Task DeletedCourseDoesNotKeepItsAssignmentsInRating()
    {
        var ids = new[] { Guid.NewGuid() };
        var rows = await LoadAsync(ids, new CatalogHandler(Guid.NewGuid(), courseExists: false));
        Assert.Empty(rows);
    }

    [Fact]
    public async Task CompleteCatalogIsNotSilentlyTruncatedAfterTwoThousandAssignments()
    {
        var ids = Enumerable.Range(0, 2001).Select(_ => Guid.NewGuid()).ToArray();
        var handler = new CatalogHandler(Guid.NewGuid(), courseExists: true);
        var rows = await LoadAsync(ids, handler);
        Assert.Equal(2001, rows.Count);
        Assert.Equal(2, handler.AssignmentBatches);
    }

    [Fact]
    public async Task EducationOutageDoesNotPretendEveryCourseWasDeleted()
    {
        var ids = new[] { Guid.NewGuid() };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            LoadAsync(ids, new CatalogHandler(Guid.NewGuid(), courseExists: true, courseUnavailable: true)));
    }
}
