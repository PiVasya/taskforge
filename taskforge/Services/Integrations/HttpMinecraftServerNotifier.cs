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

    private string? GetBaseUrl() => _config["MINECRAFT_SERVER_URL"]; // например: http://mc-host:8123
    private string GetPath() => _config["MINECRAFT_SERVER_LINK_PATH"] ?? "/taskforge/link";
    private string? GetApiKey() => _config["MINECRAFT_SERVER_KEY"]; // опционально

    public async Task<(bool Ok, string Message)> SendLinkCodeAsync(string nick, string code, CancellationToken ct)
    {
        var baseUrl = GetBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _log.LogWarning("MINECRAFT_SERVER_URL is not set. Skipping delivery for nick={Nick}.", nick);
            return (false, "MINECRAFT_SERVER_URL не настроен");
        }

        try
        {
            var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);

            var req = new
            {
                nick,
                code
            };

            var httpReq = new HttpRequestMessage(HttpMethod.Post, GetPath())
            {
                Content = JsonContent.Create(req)
            };

            var key = GetApiKey();
            if (!string.IsNullOrWhiteSpace(key))
                httpReq.Headers.TryAddWithoutValidation("X-Server-Key", key);

            var resp = await client.SendAsync(httpReq, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Minecraft server delivery failed: status={Status} body={Body}", (int)resp.StatusCode, body);
                return (false, $"Minecraft-сервер ответил {(int)resp.StatusCode}");
            }

            return (true, "sent");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Minecraft server delivery error for nick={Nick}", nick);
            return (false, "ошибка доставки");
        }
    }
}
