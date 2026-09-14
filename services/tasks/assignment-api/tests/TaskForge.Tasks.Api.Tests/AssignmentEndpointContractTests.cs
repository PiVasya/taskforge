using Microsoft.AspNetCore.Http;
using TaskForge.Tasks.Api.Endpoints;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class AssignmentEndpointContractTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    public void FreshMapQuery_AcceptsExplicitTruthyValues(string value)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?fresh=" + Uri.EscapeDataString(value));

        Assert.True(AssignmentApiEndpoints.ReadFreshMapRequest(http));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("wat")]
    public void FreshMapQuery_RejectsAnythingElse(string value)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?fresh=" + Uri.EscapeDataString(value));

        Assert.False(AssignmentApiEndpoints.ReadFreshMapRequest(http));
    }
}
