using Microsoft.Extensions.Options;
using Telegram.Bot;
using TelegramQuizBot.Configuration;

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
        var timeoutSeconds = Math.Clamp(_options.RequestTimeoutSeconds, 120, 1800);
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        return new TelegramBotClient(token, httpClient);
    }
}
