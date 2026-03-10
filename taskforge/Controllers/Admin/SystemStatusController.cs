using System.Net.Sockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Constants;

namespace taskforge.Controllers.Admin;

[ApiController]
[Route("api/admin/system-status")]
[Authorize(Roles = AppRoles.Admin)]
public sealed class SystemStatusController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<SystemStatusController> _log;

    public SystemStatusController(IConfiguration cfg, ILogger<SystemStatusController> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public sealed record ComponentStatusDto(
        string Key,
        string Name,
        bool IsHealthy,
        string Status,
        string? Details,
        long CheckedAtUnixMs,
        int? LatencyMs,
        string? Endpoint);

    public sealed record SystemStatusDto(IReadOnlyList<ComponentStatusDto> Components);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var list = new List<ComponentStatusDto>
        {
            await CheckMinecraftServerAsync(ct)
        };

        return Ok(new SystemStatusDto(list));
    }

    private async Task<ComponentStatusDto> CheckMinecraftServerAsync(CancellationToken ct)
    {
        var host = (_cfg["MINECRAFT_SERVER_HOST"] ?? _cfg["Minecraft:ServerHost"] ?? string.Empty).Trim();
        var portRaw = _cfg["MINECRAFT_SERVER_PORT"] ?? _cfg["Minecraft:ServerPort"];
        var name = (_cfg["MINECRAFT_SERVER_NAME"] ?? "Minecraft сервер").Trim();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(portRaw, out var port) || port <= 0)
        {
            return new ComponentStatusDto(
                "minecraft-server",
                name,
                false,
                "not_configured",
                "Не заданы MINECRAFT_SERVER_HOST / MINECRAFT_SERVER_PORT.",
                nowMs,
                null,
                null);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, timeoutCts.Token);
            sw.Stop();

            return new ComponentStatusDto(
                "minecraft-server",
                name,
                true,
                "online",
                "TCP соединение установлено.",
                nowMs,
                (int)sw.ElapsedMilliseconds,
                $"{host}:{port}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogWarning(ex, "Minecraft server health check failed for {Host}:{Port}", host, port);
            return new ComponentStatusDto(
                "minecraft-server",
                name,
                false,
                "offline",
                ex.Message,
                nowMs,
                (int)sw.ElapsedMilliseconds,
                $"{host}:{port}");
        }
    }
}
