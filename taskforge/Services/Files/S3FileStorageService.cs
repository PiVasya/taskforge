using Amazon.S3;
using Amazon.S3.Model;

namespace taskforge.Services.Files;

public sealed class S3FileStorageService : IFileStorageService
{
    private readonly IAmazonS3 _s3;
    private readonly IConfiguration _cfg;
    private readonly ILogger<S3FileStorageService> _log;

    public S3FileStorageService(IAmazonS3 s3, IConfiguration cfg, ILogger<S3FileStorageService> log)
    {
        _s3 = s3;
        _cfg = cfg;
        _log = log;
    }

    private string Bucket => _cfg["S3:Bucket"] ?? _cfg["S3__Bucket"] ?? "";

    private static string NormalizeFolder(string folder)
    {
        folder = (folder ?? string.Empty).Trim();
        folder = folder.Trim('/');
        return folder;
    }

    private static string MakeKey(string folder, string extension)
    {
        folder = NormalizeFolder(folder);
        extension = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension.Trim();
        if (!extension.StartsWith('.')) extension = "." + extension;

        var file = Guid.NewGuid().ToString("N") + extension;
        return string.IsNullOrEmpty(folder) ? file : $"{folder}/{file}";
    }

    public async Task<string> UploadImageAsync(IFormFile file, string folder, CancellationToken ct = default)
    {
        if (file is null || file.Length <= 0)
            throw new ArgumentException("Empty file", nameof(file));

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";

        var key = MakeKey(folder, ext);
        await using var stream = file.OpenReadStream();

        var req = new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = stream,
            ContentType = file.ContentType,
        };

        _log.LogInformation("[S3] PUT {Key} ({ContentType}, {Size} bytes)", key, file.ContentType, file.Length);
        await _s3.PutObjectAsync(req, ct);
        return key;
    }

    public async Task<string> UploadBytesAsync(byte[] bytes, string contentType, string folder, string fileExtension = ".bin", CancellationToken ct = default)
    {
        if (bytes is null || bytes.Length == 0)
            throw new ArgumentException("Empty bytes", nameof(bytes));

        var key = MakeKey(folder, fileExtension);
        await using var ms = new MemoryStream(bytes);

        var req = new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = ms,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
        };

        _log.LogInformation("[S3] PUT {Key} ({ContentType}, {Size} bytes)", key, req.ContentType, bytes.Length);
        await _s3.PutObjectAsync(req, ct);
        return key;
    }

    public async Task<(Stream Stream, string ContentType)> GetAsync(string key, CancellationToken ct = default)
    {
        var req = new GetObjectRequest
        {
            BucketName = Bucket,
            Key = key,
        };

        var resp = await _s3.GetObjectAsync(req, ct);
        return (resp.ResponseStream, resp.Headers.ContentType);
    }
}
