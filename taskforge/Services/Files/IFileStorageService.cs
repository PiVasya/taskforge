using Microsoft.AspNetCore.Http;

namespace taskforge.Services.Files;

public interface IFileStorageService
{
    Task<(string key, string contentType)> UploadImageAsync(IFormFile file, CancellationToken ct = default);
    Task<(Stream stream, string contentType)> GetAsync(string key, CancellationToken ct = default);
}
