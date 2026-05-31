using System.Diagnostics;
using System.Text.Json;
using System.Net.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Constants;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Integrations
{
    [ApiController]
    [Route("api/integrations/minecraft/chat")]
    public sealed class MinecraftChatController : ControllerBase
    {
        private readonly IMinecraftChatService _chat;
        private readonly ICurrentUserService _current;
        private readonly IConfiguration _cfg;
        private readonly IHttpClientFactory _httpFactory;

        public MinecraftChatController(IMinecraftChatService chat, ICurrentUserService current, IConfiguration cfg, IHttpClientFactory httpFactory)
        {
            _chat = chat;
            _current = current;
            _cfg = cfg;
            _httpFactory = httpFactory;
        }

        public sealed record SiteChatMessageDto(string Message);
        public sealed record IncomingMinecraftChatDto(string Nick, string? Uuid, string Message, string? Kind = null);
        public sealed record MinecraftChatMessageDto(Guid Id, string Source, string? AuthorName, string? MinecraftNick, string? MinecraftUuid, string Message, DateTime CreatedAtUtc);

        public sealed record MinecraftChatMetaDto(int? OnlinePlayers, bool Available);

        [HttpGet("messages")]
        [Authorize]
        public async Task<IActionResult> GetMessages([FromQuery] int take = 60, CancellationToken ct = default)
        {
            if (!_current.HasAnyRole(AppRoles.Admin, FeatureRoles.Minecraft))
                return Forbid();

            var list = await _chat.GetRecentAsync(take, ct);
            return Ok(list.Select(ToDto));
        }

        [HttpGet("meta")]
        [Authorize]
        public async Task<IActionResult> GetMeta(CancellationToken ct = default)
        {
            if (!_current.HasAnyRole(AppRoles.Admin, FeatureRoles.Minecraft))
                return Forbid();

            var baseUrl = (_cfg["MINECRAFT_WEBHOOK_BASE_URL"] ?? _cfg["MINECRAFT_SERVER_URL"] ?? string.Empty).Trim();
            var healthUrl = (_cfg["MINECRAFT_HEALTH_URL"] ?? string.Empty).Trim();
            var endpoint = !string.IsNullOrWhiteSpace(healthUrl)
                ? healthUrl
                : (!string.IsNullOrWhiteSpace(baseUrl) ? new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "health").ToString() : null);

            if (string.IsNullOrWhiteSpace(endpoint))
                return Ok(new MinecraftChatMetaDto(null, false));

            try
            {
                var client = _httpFactory.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    return Ok(new MinecraftChatMetaDto(null, false));

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                int? onlinePlayers = null;
                if (doc.RootElement.TryGetProperty("onlinePlayers", out var onlineEl) && onlineEl.ValueKind == JsonValueKind.Number && onlineEl.TryGetInt32(out var onlineValue))
                    onlinePlayers = onlineValue;

                return Ok(new MinecraftChatMetaDto(onlinePlayers, true));
            }
            catch
            {
                return Ok(new MinecraftChatMetaDto(null, false));
            }
        }

        [HttpPost("messages")]
        [Authorize]
        public async Task<IActionResult> PostSiteMessage([FromBody] SiteChatMessageDto dto, CancellationToken ct = default)
        {
            if (!_current.HasAnyRole(AppRoles.Admin, FeatureRoles.Minecraft))
                return Forbid();

            var created = await _chat.AddSiteMessageAsync(_current.GetUserId(), _current.IsAdmin(), dto.Message, ct);
            return Ok(ToDto(created));
        }

        [HttpPost("bridge/incoming")]
        [AllowAnonymous]
        public async Task<IActionResult> PostIncomingMinecraft([FromBody] IncomingMinecraftChatDto dto, CancellationToken ct = default)
        {
            if (!IsPluginAuthorized()) return Unauthorized();
            if (ShouldIgnoreIncoming(dto)) return Ok(new { ignored = true });

            var created = await _chat.AddMinecraftMessageAsync(dto.Nick, dto.Uuid, dto.Message, dto.Kind, ct);
            return Ok(ToDto(created));
        }

        [HttpGet("bridge/pull")]
        [AllowAnonymous]
        public async Task<IActionResult> PullForMinecraft([FromQuery] DateTime? afterUtc = null, [FromQuery] int take = 50, CancellationToken ct = default)
        {
            if (!IsPluginAuthorized()) return Unauthorized();
            var list = await _chat.GetOutgoingForMinecraftAsync(afterUtc, take, ct);
            return Ok(list.Select(ToDto));
        }


        private static bool ShouldIgnoreIncoming(IncomingMinecraftChatDto dto)
        {
            if (dto is null) return true;
            var kind = (dto.Kind ?? string.Empty).Trim();
            var message = (dto.Message ?? string.Empty).Trim();
            if (!kind.Equals("advancement", StringComparison.OrdinalIgnoreCase)) return false;
            if (message.Length == 0) return true;

            var lower = message.ToLowerInvariant();
            return lower.Contains("recipes/") || lower.Contains("/root");
        }

        private bool IsPluginAuthorized()
        {
            var key = _cfg["MINECRAFT_PLUGIN_KEY"] ?? _cfg["MINECRAFT_SERVER_KEY"];
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (!Request.Headers.TryGetValue("X-Minecraft-Key", out var got)) return false;
            return string.Equals(got.ToString(), key, StringComparison.Ordinal);
        }

        private static MinecraftChatMessageDto ToDto(MinecraftChatMessage x)
            => new(x.Id, x.Source, x.AuthorName, x.MinecraftNick, x.MinecraftUuid, x.Message, x.CreatedAtUtc);
    }
}
