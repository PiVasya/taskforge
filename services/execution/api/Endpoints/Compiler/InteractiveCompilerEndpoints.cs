using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using TaskForge.Execution.Api.Contracts;
using TaskForge.Execution.Api.Services.Interactive;
using static TaskForge.Execution.Api.Services.Results.ExecutionApiResultsService;
using static TaskForge.Execution.Api.Services.Serialization.ExecutionApiSerializationService;

namespace TaskForge.Execution.Api.Endpoints;

internal static partial class ExecutionApiEndpoints
{
    private const int MaxInteractiveCodeBytes = 1024 * 1024;
    private const int MaxWebSocketMessageBytes = 128 * 1024;

    private static void MapInteractiveCompilerEndpoints(WebApplication app)
    {
        app.MapPost("/api/compiler/sessions", CreateInteractiveSessionAsync);
        app.MapGet("/api/compiler/sessions/{sessionId:guid}/socket", ProxyInteractiveSessionAsync);
    }

    private static async Task<IResult> CreateInteractiveSessionAsync(
        CreateInteractiveCompilerSessionRequest request,
        HttpContext context,
        IHttpClientFactory factory,
        IConfiguration configuration,
        InteractiveSessionRegistry registry)
    {
        var language = NormalizeLanguage(request.Language);
        if (language == "javascript")
        {
            return Results.BadRequest(new { message = "JavaScript пока не включён в онлайн-компилятор. Для него будет отдельная среда фронтенд-задач." });
        }
        if (language is not "cpp" and not "csharp" and not "java" and not "python" and not "pascal")
        {
            return Results.BadRequest(new { message = "Этот язык пока не поддерживается интерактивным компилятором." });
        }

        var code = request.Code ?? string.Empty;
        var codeBytes = Encoding.UTF8.GetByteCount(code);
        if (codeBytes == 0) return Results.BadRequest(new { message = "Введите код перед запуском." });
        if (codeBytes > MaxInteractiveCodeBytes) return Results.BadRequest(new { message = "Код слишком большой." });

        var userIdText = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(userIdText, out var userId))
        {
            return Results.Unauthorized();
        }
        var isAdmin = TaskForgeRequestSecurity.HasAnyRole(context.User, "Admin");
        var runnerService = RunnerService(language, image: false);
        if (runnerService is null) return Results.BadRequest(new { message = "Для языка не найден раннер." });

        var analyzerClient = factory.CreateClient();
        analyzerClient.Timeout = TimeSpan.FromSeconds(35);
        var analysis = await RequestCodeAttestationAsync(language, code, "standard", analyzerClient, configuration);
        if (analysis.Error is not null) return analysis.Error;

        var columns = Math.Clamp(request.Columns.GetValueOrDefault(110), 40, 240);
        var rows = Math.Clamp(request.Rows.GetValueOrDefault(30), 10, 80);
        var timeLimitMs = Math.Clamp(request.TimeLimitMs.GetValueOrDefault(120_000), 1_000, 120_000);
        var memoryLimitMb = Math.Clamp(request.MemoryLimitMb.GetValueOrDefault(256), 64, 512);
        var created = registry.TryCreate(
            userId,
            isAdmin,
            language,
            runnerService,
            code,
            columns,
            rows,
            timeLimitMs,
            memoryLimitMb,
            analysis.Attestation!.Value);
        if (created is null)
        {
            return Results.Conflict(new { message = isAdmin
                ? "У вас уже запущено максимальное количество консольных сессий."
                : "Сначала остановите текущую консольную сессию." });
        }

        var (payload, ticket) = created.Value;
        return Results.Ok(new
        {
            sessionId = payload.Id,
            websocketUrl = $"/api/compiler/sessions/{payload.Id}/socket?ticket={ticket}",
            expiresAt = payload.ExpiresAt,
            language = payload.Language,
            limits = new
            {
                timeLimitMs = payload.TimeLimitMs,
                memoryLimitMb = payload.MemoryLimitMb,
                outputBytes = 2 * 1024 * 1024
            }
        });
    }

    private static async Task ProxyInteractiveSessionAsync(
        HttpContext context,
        Guid sessionId,
        InteractiveSessionRegistry registry,
        ILoggerFactory loggerFactory)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { message = "Ожидалось WebSocket-подключение." });
            return;
        }
        var ticket = context.Request.Query["ticket"].ToString();
        if (!registry.TryActivate(sessionId, ticket, out var session))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var logger = loggerFactory.CreateLogger("InteractiveCompilerProxy");
        using var browserSocket = await context.WebSockets.AcceptWebSocketAsync();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        linkedCts.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            using var runnerClient = new TcpClient { NoDelay = true };
            try
            {
                await runnerClient.ConnectAsync(session.RunnerService, 9090, linkedCts.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Interactive runner {Runner} is unavailable", session.RunnerService);
                await SendWebSocketJsonAsync(browserSocket, new { type = "error", message = "Раннер временно недоступен. Повторите запуск позже." }, linkedCts.Token);
                await browserSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "runner unavailable", CancellationToken.None);
                return;
            }

            using var runnerStream = runnerClient.GetStream();
            var startMessage = JsonSerializer.Serialize(new
            {
                type = "start",
                language = session.Language,
                code = session.Code,
                columns = session.Columns,
                rows = session.Rows,
                timeLimitMs = session.TimeLimitMs,
                memoryLimitMb = session.MemoryLimitMb,
                attestation = session.Attestation
            });
            var startBytes = Encoding.UTF8.GetBytes(startMessage + "\n");
            await runnerStream.WriteAsync(startBytes, linkedCts.Token);
            await runnerStream.FlushAsync(linkedCts.Token);

            var runnerToBrowser = RelayRunnerToBrowserAsync(runnerStream, browserSocket, linkedCts.Token);
            var browserToRunner = RelayBrowserToRunnerAsync(browserSocket, runnerStream, linkedCts.Token);
            var completed = await Task.WhenAny(runnerToBrowser, browserToRunner);

            if (completed == runnerToBrowser)
            {
                try { await runnerToBrowser; } catch (OperationCanceledException) { }
                if (browserSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await browserSocket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "runner session ended",
                            CancellationToken.None);
                    }
                    catch { }
                }
            }

            linkedCts.Cancel();
            try { await Task.WhenAll(runnerToBrowser, browserToRunner); } catch { }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Interactive compiler proxy session {SessionId} failed", sessionId);
        }
        finally
        {
            registry.Release(sessionId);
            if (browserSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await browserSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "session closed", CancellationToken.None); } catch { }
            }
        }
    }

    private static async Task RelayRunnerToBrowserAsync(Stream runnerStream, WebSocket browserSocket, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(runnerStream, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        while (!cancellationToken.IsCancellationRequested && browserSocket.State == WebSocketState.Open)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (Encoding.UTF8.GetByteCount(line) > MaxWebSocketMessageBytes) continue;
            var bytes = Encoding.UTF8.GetBytes(line);
            await browserSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
    }

    private static async Task RelayBrowserToRunnerAsync(WebSocket browserSocket, Stream runnerStream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested && browserSocket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await browserSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var stop = Encoding.UTF8.GetBytes("{\"type\":\"stop\"}\n");
                    await runnerStream.WriteAsync(stop, cancellationToken);
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Text) continue;
                await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
                if (message.Length > MaxWebSocketMessageBytes) return;
            } while (!result.EndOfMessage);

            if (message.Length == 0) continue;
            await runnerStream.WriteAsync(message.GetBuffer().AsMemory(0, (int)message.Length), cancellationToken);
            await runnerStream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await runnerStream.FlushAsync(cancellationToken);
        }
    }

    private static async Task SendWebSocketJsonAsync(WebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}
