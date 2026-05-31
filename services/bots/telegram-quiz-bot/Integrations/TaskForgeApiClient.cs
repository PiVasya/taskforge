using Microsoft.Extensions.Options;
using TelegramQuizBot.Configuration;

namespace TelegramQuizBot.Integrations;

public sealed class TaskForgeApiClient
{
    private readonly HttpClient _http;
    private readonly TaskForgeOptions _options;

    public TaskForgeApiClient(HttpClient http, IOptions<TaskForgeOptions> options)
    {
        _http = http;
        _options = options.Value;
        _http.BaseAddress = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(_options.InternalKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Internal-Key", _options.InternalKey);
    }

    // Первый микросервис пока самодостаточный. Этот клиент нужен для следующих этапов:
    // привязка Telegram user_id к пользователю TaskForge и отправка результатов в основное API.
    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);
}
