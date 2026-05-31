namespace TelegramQuizBot.Configuration;

public sealed class TaskForgeOptions
{
    public string ApiBaseUrl { get; set; } = "http://api:8080";
    public string InternalKey { get; set; } = string.Empty;
}
