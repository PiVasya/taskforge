namespace taskforge.Services.Files;

public interface IFileStorageService
{
    Task<string> UploadImageAsync(IFormFile file, string folder, CancellationToken ct = default);

    Task<string> UploadFileAsync(IFormFile file, string folder, CancellationToken ct = default);

    Task<string> UploadBytesAsync(
        byte[] bytes,
        string contentType,
        string folder,
        string fileExtension = ".bin",
        CancellationToken ct = default);

    Task<(Stream Stream, string ContentType)> GetAsync(string key, CancellationToken ct = default);
}
