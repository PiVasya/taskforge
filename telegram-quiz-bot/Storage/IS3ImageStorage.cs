namespace TelegramQuizBot.Storage;

public interface IS3ImageStorage
{
    Task<string> SaveImageAsync(Stream stream, string contentType, string extension, CancellationToken ct);
}
