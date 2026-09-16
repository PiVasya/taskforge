using Xunit;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace TaskForge.Solutions.RatingWorker.Tests;

public sealed class TasksApiRetryPolicyTests
{
    [Fact]
    public async Task RetriesTransientHttpStatusUntilServiceRecovers()
    {
        var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        using var response = await TasksApiRetryPolicy.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Get, "http://tasks-api/health/ready"),
            NullLogger.Instance,
            "test",
            CancellationToken.None,
            maxAttempts: 4,
            delayFactory: _ => TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task RetriesConnectionFailureUntilServiceRecovers()
    {
        var handler = new SequenceHandler(
            _ => throw new HttpRequestException("connection refused"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        using var response = await TasksApiRetryPolicy.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Get, "http://tasks-api/health/ready"),
            NullLogger.Instance,
            "test",
            CancellationToken.None,
            maxAttempts: 3,
            delayFactory: _ => TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task DoesNotRetryPermanentClientError()
    {
        var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        using var response = await TasksApiRetryPolicy.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Get, "http://tasks-api/health/ready"),
            NullLogger.Instance,
            "test",
            CancellationToken.None,
            maxAttempts: 3,
            delayFactory: _ => TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _steps;
        private int _calls;

        public SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
        {
            _steps = steps;
        }

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var index = Math.Min(call - 1, _steps.Length - 1);
            return Task.FromResult(_steps[index](request));
        }
    }
}
