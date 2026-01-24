using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace taskforge.Services.Files;

/// <summary>
/// Хранилище файлов через S3 API (MinIO).
/// Сейчас используется для картинок в условиях задач (TipTap editor).
/// </summary>
public sealed class S3FileStorageService : IFileStorageService
{
    private readonly IAmazonS3 _s3;
    private readonly S3StorageOptions _opt;
    private bool _bucketEnsured = false;

    public S3FileStorageService(IAmazonS3 s3, IOptions<S3StorageOptions> opt)
    {
        _s3 = s3;
        _opt = opt.Value;
    }

    public async Task<(string key, string contentType)> UploadImageAsync(IFormFile file, CancellationToken ct = default)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));

        var ctType = (file.ContentType ?? string.Empty).ToLowerInvariant();
        var allowed = new HashSet<string>
        {
            "image/png",
            "image/jpeg",
            "image/jpg",
            "image/webp",
            "image/gif"
        };

        if (!allowed.Contains(ctType))
            throw new ValidationException("Разрешены только изображения: png, jpg, webp, gif.");

        // лимит на размер (можно вынести в конфиг)
        const long maxBytes = 10 * 1024 * 1024;
        if (file.Length <= 0) throw new ValidationException("Файл пустой.");
        if (file.Length > maxBytes) throw new ValidationException("Файл слишком большой (макс. 10MB).");

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ctType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/jpg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".bin"
            };
        }

        var now = DateTime.UtcNow;
        var key = $"task-content/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{ext.ToLowerInvariant()}";

        await EnsureBucketAsync(ct);

        await using var stream = file.OpenReadStream();

        var req = new PutObjectRequest
        {
            BucketName = _opt.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = ctType,
            AutoCloseStream = false
        };

        await _s3.PutObjectAsync(req, ct);
        return (key, ctType);
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketEnsured) return;
        if (string.IsNullOrWhiteSpace(_opt.Bucket)) throw new ValidationException("S3 bucket is not configured");

        var exists = await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(_s3, _opt.Bucket);
        if (!exists)
        {
            await _s3.PutBucketAsync(new PutBucketRequest { BucketName = _opt.Bucket }, ct);
        }

        _bucketEnsured = true;
    }

    public async Task<(Stream stream, string contentType)> GetAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ValidationException("Ключ файла пустой.");

        var resp = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _opt.Bucket,
            Key = key
        }, ct);

        // Важно: не закрывать stream до отдачи в ответ.
        return (resp.ResponseStream, resp.Headers.ContentType ?? "application/octet-stream");
    }
}
