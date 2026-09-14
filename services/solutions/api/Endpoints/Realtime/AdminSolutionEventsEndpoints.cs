using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TaskForge.Solutions.Api.Services.Realtime;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private static WebApplication MapAdminSolutionEventsEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/solution-events", async (HttpContext http, AdminSolutionEventBroker broker, CancellationToken ct) =>
        {
            if (!broker.IsAvailable)
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await http.Response.WriteAsJsonAsync(new { code = "SOLUTION_LIVE_UNAVAILABLE", message = "Live solution feed is temporarily unavailable." }, ct);
                return;
            }

            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            http.Response.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-cache, no-store";
            http.Response.Headers.Pragma = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var lastEventId = http.Request.Headers["Last-Event-ID"].ToString();
            var subscription = broker.Subscribe(lastEventId);
            try
            {
                await http.Response.WriteAsync("retry: 3000\n\n", ct);
                foreach (var evt in subscription.Replay)
                {
                    await WriteSseEvent(http.Response, evt.EventId, "solution", evt, ct);
                }
                await http.Response.Body.FlushAsync(ct);

                while (!ct.IsCancellationRequested)
                {
                    var readyTask = subscription.Reader.WaitToReadAsync(ct).AsTask();
                    var keepAliveTask = Task.Delay(TimeSpan.FromSeconds(15), ct);
                    var completed = await Task.WhenAny(readyTask, keepAliveTask);

                    if (completed == keepAliveTask)
                    {
                        await http.Response.WriteAsync(": keepalive\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        continue;
                    }

                    if (!await readyTask) break;
                    while (subscription.Reader.TryRead(out var evt))
                    {
                        await WriteSseEvent(http.Response, evt.EventId, "solution", evt, ct);
                    }
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                broker.Unsubscribe(subscription.Id);
            }
        });

        return app;
    }

    private static async Task WriteSseEvent(HttpResponse response, string id, string eventName, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await response.WriteAsync($"id: {id}\nevent: {eventName}\ndata: {json}\n\n", ct);
    }
}
