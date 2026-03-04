using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace taskforge.Services.Integrations;

/// <summary>
/// Тонкий клиент для отправки "nick+code" на Minecraft-сервер.
/// Плагина ещё нет — но URL/порт задаются через .env, и мы будем логировать попытки.
/// </summary>
public sealed class HttpMinecraftServerNotifier : IMinecraftServerNotifier
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<HttpMinecraftServerNotifier> _log;

    public HttpMinecraftServerNotifier(IHttpClientFactory httpFactory, IConfiguration config, ILogger<HttpMinecraftServerNotifier> log)
    {
        _httpFactory = httpFactory;
        _config = config;
        _log = log;
    }

    // Новые (актуальные) переменные окружения (docker-compose/.env):
    // - MINECRAFT_WEBHOOK_BASE_URL        например: http://109.67.174.156:25566
    // - MINECRAFT_WEBHOOK_SEND_CODE_PATH  например: /taskforge/link/send
    // - MINECRAFT_WEBHOOK_KEY             shared-secret (плагин проверяет X-TaskForge-Key)
    //
    // На всякий случай поддерживаем и старые имена (MINECRAFT_SERVER_*), чтобы не ломать локальные стенды.
    private string? GetBaseUrl() =>
        _config["MINECRAFT_WEBHOOK_BASE_URL"] ??
        _config["MINECRAFT_SERVER_URL"]; // legacy

    private string GetPath() =>
        _config["MINECRAFT_WEBHOOK_SEND_CODE_PATH"] ??
        _config["MINECRAFT_SERVER_LINK_PATH"] ?? // legacy
        "/taskforge/link/send";

    private string? GetApiKey() =>
        _config["MINECRAFT_WEBHOOK_KEY"] ??
        _config["MINECRAFT_SERVER_KEY"]; // legacy

    private sealed class PluginSendCodeResponse
    {
        public bool Delivered { get; set; }
        public bool Online { get; set; }
        public string? Reason { get; set; }
        public string? Uuid { get; set; }
        public bool Duplicate { get; set; }
    }

    public async Task<(bool Ok, string Message)> SendLinkCodeAsync(string nick, string code, CancellationToken ct)
    {
        var baseUrl = GetBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _log.LogWarning("Minecraft webhook base url is not set. Skipping delivery for nick={Nick}.", nick);
            return (false, "Webhook Minecraft не настроен");
        }

        try
        {
            var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);

            // Плагин ждёт:
            // - Header: X-TaskForge-Key
            // - Header: X-Request-Id (для идемпотентности)
            // - JSON: { nick, code, ttlSeconds }
            var reqId = Guid.NewGuid().ToString();
            var req = new { nick, code, ttlSeconds = 600 };

            var httpReq = new HttpRequestMessage(HttpMethod.Post, GetPath())
            {
                Content = JsonContent.Create(req)
            };

            httpReq.Headers.TryAddWithoutValidation("X-Request-Id", reqId);
            var key = GetApiKey();
            if (!string.IsNullOrWhiteSpace(key))
                httpReq.Headers.TryAddWithoutValidation("X-TaskForge-Key", key);

            var resp = await client.SendAsync(httpReq, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning(
                    "Minecraft webhook delivery failed: nick={Nick} status={Status} body={Body} requestId={RequestId}",
                    nick, (int)resp.StatusCode, body, reqId);
                return (false, $"Webhook ответил {(int)resp.StatusCode}");
            }

            // Плагин может вернуть 200, но delivered=false (например, игрок оффлайн).
            // Поэтому читаем JSON и возвращаем корректный результат.
            try
            {
                var dto = await resp.Content.ReadFromJsonAsync<PluginSendCodeResponse>(cancellationToken: ct);
                if (dto == null)
                    return (true, "sent");

                if (dto.Duplicate)
                    return (true, "duplicate");

                if (dto.Delivered)
                    return (true, "delivered");

                // delivered=false
                var reason = string.IsNullOrWhiteSpace(dto.Reason) ? "not delivered" : dto.Reason;
                return (false, reason);
            }
            catch
            {
                // если плагин вернул не-JSON — не ломаем процесс
                return (true, "sent");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Minecraft webhook delivery error for nick={Nick}", nick);
            return (false, "ошибка доставки");
        }
    }
}
