using Microsoft.AspNetCore.Http;

namespace taskforge.Services.Files;

public interface IFileStorageService
{
    Task<(string key, string contentType)> UploadImageAsync(IFormFile file, CancellationToken ct = default);
    Task<(string key, string contentType)> UploadImageAsync(IFormFile file, string prefix, CancellationToken ct = default);
    Task<(Stream stream, string contentType)> GetAsync(string key, CancellationToken ct = default);
}
