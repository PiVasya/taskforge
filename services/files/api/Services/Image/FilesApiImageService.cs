using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TaskForge.Files.Api.Data;
using TaskForge.Files.Api.Domain;

using TaskForge.Files.Api.Contracts;
using static TaskForge.Files.Api.Services.Access.FilesApiAccessService;
using static TaskForge.Files.Api.Services.Common.FilesApiCommonService;
using static TaskForge.Files.Api.Services.Mapping.FilesApiMappingService;
using static TaskForge.Files.Api.Services.Serialization.FilesApiSerializationService;

namespace TaskForge.Files.Api.Services.Image;

internal static class FilesApiImageService
{
    internal static async Task<IResult> UploadImageFromRequest(HttpRequest req, FilesDbContext db, IAmazonS3 s3, IConfiguration cfg, ILoggerFactory loggerFactory, bool allowPrivateFolders, CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Files.UploadImage");
        IFormCollection form;
        IFormFile? file;
        try
        {
            form = await req.ReadFormAsync(ct);
            file = form.Files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            return Problem(400, "FORM_READ_FAILED", "request.form", "Не удалось прочитать multipart/form-data. Проверьте, что файл отправлен полем form-data `file`.", ex.Message);
        }

        if (file == null) return Problem(400, "FILE_REQUIRED", "request.validation", "Файл не передан. Выберите изображение и повторите загрузку.");
        if (file.Length <= 0) return Problem(400, "EMPTY_FILE", "request.validation", "Файл пустой. Выберите другое изображение.");
        var maxBytes = MaxUploadBytes(cfg);
        if ((req.ContentLength ?? 0) > maxBytes + 1024 * 1024 || file.Length > maxBytes)
            return Problem(413, "FILE_TOO_LARGE", "request.validation", $"Файл слишком большой. Максимальный размер изображения: {maxBytes / 1024 / 1024} МБ.");

        var validation = await ValidateImageFileAsync(file, ct);
        if (!validation.Ok) return Problem(400, validation.Code ?? "UNSUPPORTED_FILE_TYPE", "request.validation", validation.Message ?? "Формат изображения не поддерживается.", file.ContentType);

        var bucket = Bucket(cfg);
        var folder = NormalizeFolder((form.TryGetValue("folder", out var f) ? f.ToString() : null) ?? "editor-images");
        if (!allowPrivateFolders && !IsEditorOrAdmin(req.HttpContext) && !IsPublicUploadFolder(folder))
            return Problem(403, "FILE_FOLDER_FORBIDDEN", "request.authorization", "Обычный пользователь может загружать изображения только в публичный editor-images namespace.", folder);
        if (!allowPrivateFolders && IsPrivateFileKey(folder + "/") && !IsEditorOrAdmin(req.HttpContext))
            return Problem(403, "PRIVATE_FILE_FOLDER_FORBIDDEN", "request.authorization", "Загрузка в приватные judge/storage префиксы доступна только редактору или внутреннему сервису.", folder);
        var key = MakeKey(folder, validation.Extension ?? ".bin");

        var ensure = await TryEnsureBucket(s3, cfg, createIfMissing: true, ct);
        if (!ensure.Ok) return Problem(503, ensure.Code, ensure.Stage, ensure.Message, ensure.Detail);

        try
        {
            await using var input = file.OpenReadStream();
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = input,
                ContentType = validation.ContentType ?? "application/octet-stream",
                AutoCloseStream = false
            }, ct);
            log.LogInformation("Uploaded {Key} to MinIO bucket {Bucket}: {Size} bytes", key, bucket, file.Length);
        }
        catch (AmazonS3Exception ex)
        {
            return Problem(503, "MINIO_PUT_OBJECT_FAILED", "storage.put_object", "MinIO принял подключение, но не смог сохранить объект. Проверьте права bucket/access key и состояние MinIO.", ex.Message);
        }
        catch (Exception ex)
        {
            return Problem(503, "FILE_UPLOAD_FAILED", "storage.put_object", "Не удалось загрузить файл в MinIO.", ex.Message);
        }

        var item = new StoredFile
        {
            FileName = Path.GetFileName(file.FileName),
            ContentType = validation.ContentType ?? "application/octet-stream",
            Size = file.Length,
            Url = key
        };

        try
        {
            db.Files.Add(item);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            return Problem(503, "FILE_METADATA_SAVE_FAILED", "database.metadata_save", "Файл загружен в MinIO, но не удалось сохранить метаданные в БД. Повторите действие или проверьте files-api/PostgreSQL.", ex.Message);
        }

        return Microsoft.AspNetCore.Http.Results.Ok(new
        {
            item.Id,
            item.FileName,
            item.ContentType,
            item.Size,
            key,
            url = IsPublicFileKey(key) ? $"/api/files/{Uri.EscapeDataString(key)}" : null,
            privateUrl = $"/api/private-files/{Uri.EscapeDataString(key)}",
            storage = "minio"
        });
    }

    internal static async Task<ImageValidationResult> ValidateImageFileAsync(IFormFile file, CancellationToken ct)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        await using var stream = file.OpenReadStream();
        var header = new byte[System.Math.Min(32, System.Math.Max(0, (int)System.Math.Min(file.Length, 32)))] ;
        var read = header.Length == 0 ? 0 : await stream.ReadAsync(header.AsMemory(0, header.Length), ct);
        var detected = DetectImage(header.AsSpan(0, read), ext);
        if (detected is null)
        {
            return new ImageValidationResult(false, null, null, "UNSUPPORTED_FILE_TYPE", "Можно загружать только растровые изображения PNG, JPEG, WEBP, GIF, BMP или PPM. SVG намеренно запрещён для безопасности.");
        }
        return new ImageValidationResult(true, detected.Value.ContentType, detected.Value.Extension, null, null);
    }

    internal static (string ContentType, string Extension)? DetectImage(ReadOnlySpan<byte> header, string ext)
    {
        if (header.Length >= 8 && header[0] == 0x89 && header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G') return ("image/png", ".png");
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ("image/jpeg", ".jpg");
        if (header.Length >= 12 && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P') return ("image/webp", ".webp");
        if (header.Length >= 6 && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F') return ("image/gif", ".gif");
        if (header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M') return ("image/bmp", ".bmp");
        if (header.Length >= 2 && header[0] == (byte)'P' && (header[1] == (byte)'3' || header[1] == (byte)'6')) return ("image/x-portable-pixmap", ".ppm");
        // Do not trust extension/content-type when bytes were readable.
        if (header.Length > 0) return null;
        return ext is ".png" ? ("image/png", ".png")
            : ext is ".jpg" or ".jpeg" ? ("image/jpeg", ".jpg")
            : ext is ".webp" ? ("image/webp", ".webp")
            : ext is ".gif" ? ("image/gif", ".gif")
            : ext is ".bmp" ? ("image/bmp", ".bmp")
            : ext is ".ppm" ? ("image/x-portable-pixmap", ".ppm")
            : null;
    }

    internal static async Task<IResult> Download(string key, HttpContext http, IAmazonS3 s3, IConfiguration cfg, bool publicRoute, bool internalRoute, CancellationToken ct)
    {
        var decoded = NormalizeKey(Uri.UnescapeDataString(key ?? string.Empty));
        if (string.IsNullOrWhiteSpace(decoded)) return Problem(400, "FILE_KEY_REQUIRED", "request.validation", "Не указан ключ файла.");
        if (publicRoute && !IsPublicFileKey(decoded)) return Problem(404, "FILE_NOT_FOUND", "storage.public_policy", "Файл не найден или не является публичным.");
        if (!publicRoute && !internalRoute && !CanReadPrivateKey(http, decoded)) return Problem(403, "PRIVATE_FILE_FORBIDDEN", "request.authorization", "У вас нет доступа к этому файлу.");
        var ensure = await TryEnsureBucket(s3, cfg, createIfMissing: true, ct);
        if (!ensure.Ok) return Problem(503, ensure.Code, ensure.Stage, ensure.Message, ensure.Detail);
        try
        {
            var resp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket(cfg), Key = decoded }, ct);
            var fileName = Path.GetFileName(decoded);
            http.Response.Headers.CacheControl = publicRoute
                ? "public,max-age=31536000,immutable"
                : "private,max-age=0,no-store";
            return Microsoft.AspNetCore.Http.Results.File(resp.ResponseStream, string.IsNullOrWhiteSpace(resp.Headers.ContentType) ? "application/octet-stream" : resp.Headers.ContentType, fileName, enableRangeProcessing: true);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Problem(404, "FILE_NOT_FOUND", publicRoute ? "storage.get_public_object" : "storage.get_private_object", "Файл не найден в MinIO. Возможно, объект был удалён или ссылка устарела.", decoded);
        }
        catch (AmazonS3Exception ex)
        {
            return Problem(503, "MINIO_GET_OBJECT_FAILED", publicRoute ? "storage.get_public_object" : "storage.get_private_object", "Не удалось получить файл из MinIO. Проверьте bucket, права доступа и состояние MinIO.", ex.Message);
        }
        catch (Exception ex)
        {
            return Problem(503, "FILE_DOWNLOAD_FAILED", publicRoute ? "storage.get_public_object" : "storage.get_private_object", "Не удалось скачать файл из хранилища.", ex.Message);
        }
    }

    internal static bool IsPublicUploadFolder(string folder) => NormalizeKey(folder).StartsWith("editor-images/", StringComparison.OrdinalIgnoreCase) || string.Equals(NormalizeKey(folder), "editor-images", StringComparison.OrdinalIgnoreCase) || NormalizeKey(folder).StartsWith("public/", StringComparison.OrdinalIgnoreCase) || string.Equals(NormalizeKey(folder), "public", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPublicFileKey(string key) => NormalizeKey(key).StartsWith("editor-images/", StringComparison.OrdinalIgnoreCase) || NormalizeKey(key).StartsWith("public/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPrivateFileKey(string key) => NormalizeKey(key).StartsWith("image-tests/", StringComparison.OrdinalIgnoreCase) || NormalizeKey(key).StartsWith("agent-conversations/", StringComparison.OrdinalIgnoreCase);

    internal static long MaxUploadBytes(IConfiguration cfg) => cfg.GetValue<long?>("Files:MaxUploadBytes") ?? cfg.GetValue<long?>("Files:MaxImageUploadBytes") ?? 10L * 1024 * 1024;

}
