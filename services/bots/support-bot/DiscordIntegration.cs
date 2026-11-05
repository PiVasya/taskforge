using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TaskForge.SupportBot;

// Receives user-initiated Discord DMs through Gateway (not an HTTP webhook).
// Uses only the Direct Messages intent; server messages and privileged intents are not needed.
public sealed class DiscordIntegration(IConfiguration cfg, IHttpClientFactory factory, ILogger<DiscordIntegration> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int DirectMessagesIntent = 1 << 12;
    private const int MaxGatewayMessageBytes = 1024 * 1024;
    private volatile bool _connected;

    private string? Token => cfg["Discord:BotToken"];
    public bool Configured => !string.IsNullOrWhiteSpace(Token);
    public bool GatewayConnected => _connected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Configured)
        {
            logger.LogInformation("Discord integration disabled; Discord:BotToken is not configured.");
            return;
        }

        var session = new GatewaySession();
        var delaySeconds = 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectGatewayAsync(session, stoppingToken);
                delaySeconds = 2;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discord gateway disconnected; reconnecting.");
                delaySeconds = Math.Min(60, delaySeconds * 2);
            }
            finally { _connected = false; }

            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ConnectGatewayAsync(GatewaySession session, CancellationToken stoppingToken)
    {
        var gatewayUrl = session.Url ?? "wss://gateway.discord.gg";
        var uri = new Uri(gatewayUrl.TrimEnd('/') + "/?v=10&encoding=json");
        if (uri.Scheme != "wss" || !(uri.Host == "discord.gg" || uri.Host.EndsWith(".discord.gg", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Discord gateway URL is not a Discord endpoint.");

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri, stoppingToken);
        using var sendGate = new SemaphoreSlim(1, 1);
        using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var incoming = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var consumer = ConsumeIncomingAsync(incoming.Reader, stoppingToken);
        Task? heartbeats = null;
        var heartbeat = new HeartbeatTracker();
        try
        {
            while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
            {
                var frame = await ReceiveJsonAsync(socket, stoppingToken);
                if (frame == null) break;
                using var doc = frame;
                var root = doc.RootElement;
                if (!root.TryGetProperty("op", out var opValue) || !opValue.TryGetInt32(out var op)) continue;
                if (root.TryGetProperty("s", out var seq) && seq.ValueKind == JsonValueKind.Number && seq.TryGetInt64(out var number))
                    session.Sequence = number;

                if (op == 10)
                {
                    var interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                    if (interval < 1000) throw new InvalidOperationException("Discord heartbeat interval is invalid.");
                    heartbeats = HeartbeatLoopAsync(socket, sendGate, session, heartbeat, TimeSpan.FromMilliseconds(interval), connectionStop.Token);
                    if (!string.IsNullOrWhiteSpace(session.Id) && session.Sequence.HasValue)
                        await SendGatewayAsync(socket, sendGate, new { op = 6, d = new { token = Token, session_id = session.Id, seq = session.Sequence } }, stoppingToken);
                    else
                        await SendGatewayAsync(socket, sendGate, new
                        {
                            op = 2,
                            d = new { token = Token, intents = DirectMessagesIntent, properties = new { os = "linux", browser = "taskforge", device = "taskforge" } }
                        }, stoppingToken);
                }
                else if (op == 1)
                {
                    await SendGatewayAsync(socket, sendGate, new { op = 1, d = session.Sequence }, stoppingToken);
                }
                else if (op == 11) heartbeat.Acknowledged = true;
                else if (op == 7) break;
                else if (op == 9)
                {
                    if (root.TryGetProperty("d", out var resume) && resume.ValueKind == JsonValueKind.False)
                        session.Clear();
                    break;
                }
                else if (op == 0)
                {
                    var name = Text(root, "t");
                    var payload = root.GetProperty("d");
                    if (name == "READY")
                    {
                        session.Id = Text(payload, "session_id");
                        var url = Text(payload, "resume_gateway_url");
                        if (Uri.TryCreate(url, UriKind.Absolute, out var resumeUrl) && resumeUrl.Scheme == "wss" && (resumeUrl.Host == "discord.gg" || resumeUrl.Host.EndsWith(".discord.gg", StringComparison.OrdinalIgnoreCase)))
                            session.Url = resumeUrl.GetLeftPart(UriPartial.Path);
                        _connected = true;
                        logger.LogInformation("Discord gateway ready for direct messages.");
                    }
                    else if (name == "RESUMED") _connected = true;
                    else if (name == "MESSAGE_CREATE")
                    {
                        // Do not collect or forward guild/server content.
                        if ((!payload.TryGetProperty("guild_id", out var guild) || guild.ValueKind == JsonValueKind.Null)
                            && payload.TryGetProperty("author", out var author)
                            && (!author.TryGetProperty("bot", out var isBot) || isBot.ValueKind != JsonValueKind.True))
                            await incoming.Writer.WriteAsync(payload.Clone(), stoppingToken);
                    }
                }
            }
            if (socket.CloseStatus is WebSocketCloseStatus.PolicyViolation or WebSocketCloseStatus.InvalidPayloadData)
                session.Clear();
        }
        finally
        {
            _connected = false;
            incoming.Writer.TryComplete();
            connectionStop.Cancel();
            if (heartbeats != null)
            {
                try { await heartbeats; } catch (OperationCanceledException) { } catch (WebSocketException) { }
            }
            try { await consumer; } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private static async Task<JsonDocument?> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var data = new MemoryStream();
        var buffer = new byte[16384];
        ValueWebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("Unexpected Discord gateway frame.");
            if (data.Length + part.Count > MaxGatewayMessageBytes) throw new InvalidOperationException("Discord gateway frame too large.");
            data.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return JsonDocument.Parse(data.ToArray());
    }

    private static async Task SendGatewayAsync(ClientWebSocket socket, SemaphoreSlim gate, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await gate.WaitAsync(ct);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct); }
        finally { gate.Release(); }
    }

    private static async Task HeartbeatLoopAsync(ClientWebSocket socket, SemaphoreSlim gate, GatewaySession session, HeartbeatTracker heartbeat, TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await Task.Delay(interval, ct);
            if (socket.State == WebSocketState.Open)
            {
                if (!heartbeat.Acknowledged)
                {
                    socket.Abort();
                    return;
                }
                heartbeat.Acknowledged = false;
                await SendGatewayAsync(socket, gate, new { op = 1, d = session.Sequence }, ct);
            }
        }
    }

    private async Task ConsumeIncomingAsync(ChannelReader<JsonElement> reader, CancellationToken ct)
    {
        await foreach (var payload in reader.ReadAllAsync(ct))
        {
            try { await ProcessDirectMessageAsync(payload, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Discord DM processing failed."); }
        }
    }

    private async Task ProcessDirectMessageAsync(JsonElement payload, CancellationToken ct)
    {
        var author = Object(payload, "author");
        var senderId = Text(author, "id") ?? "";
        var channelId = Text(payload, "channel_id") ?? "";
        var messageId = Text(payload, "id") ?? "";
        var text = (Text(payload, "content") ?? "").Trim();
        if (!ValidSnowflake(senderId) || !ValidSnowflake(channelId) || !ValidSnowflake(messageId)) return;
        if (text.Length == 0)
        {
            await ReplyAsync(channelId, "Сейчас я принимаю только текстовые сообщения.", ct);
            return;
        }
        if (text.Length > 8000) return;
        if (text is "!help" or "!start" or "/help" or "/start")
        {
            await ReplyAsync(channelId, "Привет! Я бот TaskForge. Чтобы привязать Discord, открой Настройки → Интеграции → Discord, сгенерируй код и просто отправь мне его одним сообщением. После привязки можешь писать сюда в техподдержку.", ct);
            return;
        }

        using var lookup = InternalRequest(HttpMethod.Get, "api/internal/integrations/discord/by-subscriber?subscriberId=" + Uri.EscapeDataString(senderId));
        using var identityResponse = await factory.CreateClient("identity-api").SendAsync(lookup, ct);
        if (identityResponse.StatusCode == HttpStatusCode.NotFound)
        {
            if (LooksLikeLinkCode(text))
            {
                using var req = InternalRequest(HttpMethod.Post, "api/internal/integrations/discord/confirm",
                    new { code = text, discordUserId = senderId, name = Text(author, "global_name") ?? Text(author, "username") });
                using var resp = await factory.CreateClient("identity-api").SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode && (int)resp.StatusCode >= 500)
                {
                    await ReplyAsync(channelId, "Не удалось привязать Discord.", ct);
                    return;
                }
                var result = await ReadResponseMessageAsync(resp, ct);
                await ReplyAsync(channelId, (resp.IsSuccessStatusCode ? "✅ " : "❌ ") + (result ?? "Не удалось привязать Discord."), ct);
                return;
            }

            await ReplyAsync(channelId, "Discord ещё не привязан. Открой TaskForge → Настройки → Интеграции → Discord, сгенерируй код и отправь мне только 8 символов кода без команд.", ct);
            return;
        }
        if (!identityResponse.IsSuccessStatusCode) throw new HttpRequestException("Identity unavailable: " + (int)identityResponse.StatusCode);
        using var linked = await JsonDocument.ParseAsync(await identityResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!Guid.TryParse(Text(linked.RootElement, "userId"), out var userId)) throw new InvalidOperationException("Invalid identity response.");
        using var forward = InternalRequest(HttpMethod.Post, "api/internal/support/discord/user-message",
            new { userId, message = text, messageId });
        using var resultResponse = await factory.CreateClient("support-api").SendAsync(forward, ct);
        if (!resultResponse.IsSuccessStatusCode) throw new HttpRequestException("Support unavailable: " + (int)resultResponse.StatusCode);
        await ReplyAsync(channelId, "✅ Сообщение отправлено в техподдержку.", ct);
    }

    public async Task<DiscordSendResult> SendTextAsync(string userId, string text, CancellationToken ct)
    {
        if (!Configured) return new(false, false, "DISCORD_NOT_CONFIGURED");
        if (!ValidSnowflake(userId)) return new(false, true, "INVALID_DISCORD_USER_ID");
        using var dmRequest = CreateBotRequest(HttpMethod.Post, "users/@me/channels", new { recipient_id = userId });
        using var dmResponse = await factory.CreateClient("discord").SendAsync(dmRequest, ct);
        if (!dmResponse.IsSuccessStatusCode) return HttpError(dmResponse.StatusCode);
        using var channel = await JsonDocument.ParseAsync(await dmResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var channelId = Text(channel.RootElement, "id");
        if (!ValidSnowflake(channelId)) return new(false, false, "INVALID_DISCORD_DM_CHANNEL");
        return await SendChannelTextAsync(channelId!, text, ct);
    }

    private async Task<DiscordSendResult> SendChannelTextAsync(string channelId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(false, true, "EMPTY_DISCORD_MESSAGE");
        foreach (var part in SplitText(text, 1800))
        {
            using var request = CreateBotRequest(HttpMethod.Post, "channels/" + channelId + "/messages",
                new { content = part, allowed_mentions = new { parse = Array.Empty<string>() } });
            try
            {
                using var response = await factory.CreateClient("discord").SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return HttpError(response.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discord message delivery failed.");
                return new(false, false, "DISCORD_NETWORK_ERROR");
            }
        }
        return new(true, false, null);
    }

    private async Task ReplyAsync(string channelId, string text, CancellationToken ct)
    {
        var result = await SendChannelTextAsync(channelId, text, ct);
        if (!result.Success) logger.LogWarning("Discord DM acknowledgement failed. code={ErrorCode}", result.ErrorCode);
    }

    private HttpRequestMessage CreateBotRequest(HttpMethod method, string path, object? payload = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", Token);
        if (payload != null) request.Content = JsonContent.Create(payload, options: JsonOptions);
        return request;
    }

    private HttpRequestMessage InternalRequest(HttpMethod method, string path, object? payload = null)
    {
        var request = new HttpRequestMessage(method, path);
        var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
        if (payload != null) request.Content = JsonContent.Create(payload, options: JsonOptions);
        return request;
    }

    private static DiscordSendResult HttpError(HttpStatusCode status) =>
        new(false, status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, "DISCORD_HTTP_" + (int)status);

    public static bool ValidSnowflake(string? id) =>
        id is { Length: >= 16 and <= 20 } && id.All(c => c >= '0' && c <= '9') && ulong.TryParse(id, out var parsed) && parsed > 0;

    private static IEnumerable<string> SplitText(string text, int maxLength)
    {
        for (var i = 0; i < text.Length; i += maxLength)
            yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
    }

    private static bool LooksLikeLinkCode(string value) => value.Length == 8
        && value.All(c => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(char.ToUpperInvariant(c)));

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static JsonElement Object(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : default;

    private static async Task<string?> ReadResponseMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return Text(doc.RootElement, "message");
        }
        catch (JsonException) { return null; }
    }

    private sealed class HeartbeatTracker
    {
        public volatile bool Acknowledged = true;
    }

    private sealed class GatewaySession
    {
        public string? Id { get; set; }
        public string? Url { get; set; }
        public long? Sequence { get; set; }
        public void Clear() { Id = null; Url = null; Sequence = null; }
    }
}

public sealed record DiscordSendResult(bool Success, bool PermanentFailure, string? ErrorCode);
