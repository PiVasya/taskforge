using System.Diagnostics;
using System.Net.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace taskforge.Controllers.Admin;

[ApiController]
[Route("api/admin/system-status")]
[Authorize(Roles = "Admin")]
public sealed class SystemStatusController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _cfg;

    public SystemStatusController(IHttpClientFactory httpFactory, IConfiguration cfg)
    {
        _httpFactory = httpFactory;
        _cfg = cfg;
    }

    public sealed record ComponentStatusDto(string Code, string Name, string Status, string? Details, string? Endpoint, long? LatencyMs, DateTime CheckedAtUtc);
    public sealed record SystemStatusDto(IReadOnlyList<ComponentStatusDto> Components);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var components = new List<ComponentStatusDto>
        {
            await CheckMinecraftPluginAsync(ct)
        };

        return Ok(new SystemStatusDto(components));
    }

    private async Task<ComponentStatusDto> CheckMinecraftPluginAsync(CancellationToken ct)
    {
        var baseUrl = (_cfg["MINECRAFT_WEBHOOK_BASE_URL"] ?? _cfg["MINECRAFT_SERVER_URL"] ?? string.Empty).Trim();
        var healthUrl = (_cfg["MINECRAFT_HEALTH_URL"] ?? string.Empty).Trim();
        var endpoint = !string.IsNullOrWhiteSpace(healthUrl)
            ? healthUrl
            : (!string.IsNullOrWhiteSpace(baseUrl) ? new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "health").ToString() : null);

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ComponentStatusDto(
                "minecraft-plugin",
                "Minecraft сервер",
                "not_configured",
                "Не заданы MINECRAFT_WEBHOOK_BASE_URL или MINECRAFT_HEALTH_URL.",
                null,
                null,
                DateTime.UtcNow);
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var client = _httpFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var resp = await client.SendAsync(req, ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (body.Length > 200) body = body[..200] + "…";
                return new ComponentStatusDto(
                    "minecraft-plugin",
                    "Minecraft сервер",
                    "degraded",
                    $"Health endpoint вернул {(int)resp.StatusCode}. {body}",
                    endpoint,
                    sw.ElapsedMilliseconds,
                    DateTime.UtcNow);
            }

            return new ComponentStatusDto(
                "minecraft-plugin",
                "Minecraft сервер",
                "healthy",
                "TaskForge видит health endpoint плагина.",
                endpoint,
                sw.ElapsedMilliseconds,
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            return new ComponentStatusDto(
                "minecraft-plugin",
                "Minecraft сервер",
                "down",
                ex.Message,
                endpoint,
                null,
                DateTime.UtcNow);
        }
    }
}
