using Telegram.Bot;
using TelegramQuizBot.Configuration;
using Microsoft.Extensions.Options;

namespace TelegramQuizBot.Bot;

public sealed class TelegramBotClientFactory
{
    private readonly TelegramQuizOptions _options;

    public TelegramBotClientFactory(IOptions<TelegramQuizOptions> options)
    {
        _options = options.Value;
    }

    public TelegramBotClient Create(string token)
    {
        var timeoutSeconds = _options.TelegramRequestTimeoutSeconds <= 0
            ? 600
            : _options.TelegramRequestTimeoutSeconds;

        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        return new TelegramBotClient(token, httpClient);
    }
}
