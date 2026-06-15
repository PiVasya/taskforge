using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TaskForge.Files.Api.Data;
using TaskForge.Files.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("files-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "files-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var maxUploadBytes = builder.Configuration.GetValue<long?>("Files:MaxUploadBytes")
    ?? builder.Configuration.GetValue<long?>("Files:MaxImageUploadBytes")
    ?? 10L * 1024 * 1024;
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
    options.ValueLengthLimit = 1024 * 1024;
    options.MultipartHeadersLengthLimit = 64 * 1024;
});
builder.Services.AddDbContext<FilesDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var endpoint = Required(cfg, "S3:Endpoint", "S3__Endpoint");
    var accessKey = Required(cfg, "S3:AccessKey", "S3__AccessKey");
    var secretKey = Required(cfg, "S3:SecretKey", "S3__SecretKey");
    var region = cfg["S3:Region"] ?? cfg["S3__Region"] ?? "us-east-1";
    var forcePathStyle = cfg.GetValue("S3:UsePathStyle", true);
    return new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
    {
        ServiceURL = endpoint,
        ForcePathStyle = forcePathStyle,
        AuthenticationRegion = region,
        UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
    });
});

var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("files-api");
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var s = app.Services.CreateScope();
    var db = s.ServiceProvider.GetRequiredService<FilesDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for FilesDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for FilesDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var s = app.Services.CreateScope();
    await s.ServiceProvider.GetRequiredService<FilesDbContext>().Database.EnsureCreatedAsync();
}
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("files");

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-files-api" }));
app.MapGet("/health/ready", async (FilesDbContext db, IAmazonS3 s3, IConfiguration cfg) =>
{
    if (!await db.Database.CanConnectAsync()) return Results.Json(new { status = 503, service = "taskforge-files-api", stage = "database.connect", code = "DB_NOT_READY", message = "files-api не может подключиться к PostgreSQL." }, statusCode: 503);
    var bucketCheck = await TryEnsureBucket(s3, cfg, createIfMissing: true, default);
    return bucketCheck.Ok ? Results.Ok(new { status = "ready", service = "taskforge-files-api", storage = "minio" }) : Results.Json(new { status = 503, service = "taskforge-files-api", storage = "minio", bucketCheck.Stage, bucketCheck.Code, bucketCheck.Message, bucketCheck.Detail }, statusCode: 503);
});
app.MapGet("/", () => Results.Ok(new { service = "taskforge-files-api", database = "taskforge_files", storage = "minio", status = "files microservice active" }));
app.MapGet("/api/files/schema-owner", () => Results.Ok(new { database = "taskforge_files", ownedEntities = new[] { "StoredFile" }, storage = "MinIO/S3" }));

app.MapPost("/api/files/images", async (HttpRequest req, FilesDbContext db, IAmazonS3 s3, IConfiguration cfg, ILoggerFactory loggerFactory, CancellationToken ct) =>
    await UploadImageFromRequest(req, db, s3, cfg, loggerFactory, false, ct)).DisableAntiforgery();

app.MapPost("/api/internal/files/images", async (HttpRequest req, FilesDbContext db, IAmazonS3 s3, IConfiguration cfg, ILoggerFactory loggerFactory, CancellationToken ct) =>
    await UploadImageFromRequest(req, db, s3, cfg, loggerFactory, true, ct)).DisableAntiforgery();

app.MapGet("/api/files", async (HttpContext http, FilesDbContext db) =>
{
    if (!IsEditorOrAdmin(http)) return Results.Json(new { status = 403, code = "EDITOR_REQUIRED", message = "Список файлов доступен только редактору или администратору." }, statusCode: StatusCodes.Status403Forbidden);
    var rows = await db.Files.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync();
    return Results.Ok(rows.Select(ToDto));
});
app.MapGet("/api/files/{**key}", async (string key, HttpContext http, IAmazonS3 s3, IConfiguration cfg, CancellationToken ct) => await Download(key, http, s3, cfg, publicRoute: true, internalRoute: false, ct));
app.MapGet("/api/internal/files/{**key}", async (string key, HttpContext http, IAmazonS3 s3, IConfiguration cfg, CancellationToken ct) => await Download(key, http, s3, cfg, publicRoute: false, internalRoute: true, ct));
app.MapGet("/api/private-files/{**key}", async (string key, HttpContext http, IAmazonS3 s3, IConfiguration cfg, CancellationToken ct) => await Download(key, http, s3, cfg, publicRoute: false, internalRoute: false, ct));

app.Run();

static object ToDto(StoredFile item)
{
    var isPublic = IsPublicFileKey(item.Url);
    return new
    {
        item.Id,
        item.FileName,
        item.ContentType,
        item.Size,
        key = item.Url,
        isPublic,
        url = isPublic ? $"/api/files/{Uri.EscapeDataString(item.Url)}" : null,
        privateUrl = $"/api/private-files/{Uri.EscapeDataString(item.Url)}",
        item.CreatedAt
    };
}
static async Task<IResult> UploadImageFromRequest(HttpRequest req, FilesDbContext db, IAmazonS3 s3, IConfiguration cfg, ILoggerFactory loggerFactory, bool allowPrivateFolders, CancellationToken ct)
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

    return Results.Ok(new
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

static async Task<ImageValidationResult> ValidateImageFileAsync(IFormFile file, CancellationToken ct)
{
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    await using var stream = file.OpenReadStream();
    var header = new byte[Math.Min(32, Math.Max(0, (int)Math.Min(file.Length, 32)))] ;
    var read = header.Length == 0 ? 0 : await stream.ReadAsync(header.AsMemory(0, header.Length), ct);
    var detected = DetectImage(header.AsSpan(0, read), ext);
    if (detected is null)
    {
        return new ImageValidationResult(false, null, null, "UNSUPPORTED_FILE_TYPE", "Можно загружать только растровые изображения PNG, JPEG, WEBP, GIF, BMP или PPM. SVG намеренно запрещён для безопасности.");
    }
    return new ImageValidationResult(true, detected.Value.ContentType, detected.Value.Extension, null, null);
}

static (string ContentType, string Extension)? DetectImage(ReadOnlySpan<byte> header, string ext)
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

static async Task<IResult> Download(string key, HttpContext http, IAmazonS3 s3, IConfiguration cfg, bool publicRoute, bool internalRoute, CancellationToken ct)
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
        return Results.File(resp.ResponseStream, string.IsNullOrWhiteSpace(resp.Headers.ContentType) ? "application/octet-stream" : resp.Headers.ContentType, fileName, enableRangeProcessing: true);
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

static bool CanReadPrivateKey(HttpContext http, string key)
{
    if (IsEditorOrAdmin(http)) return true;
    var normalized = NormalizeKey(key);
    if (IsPublicFileKey(normalized)) return true;
    if (normalized.StartsWith("image-tests/reference/", StringComparison.OrdinalIgnoreCase)) return false;
    if (normalized.StartsWith("image-tests/submissions/", StringComparison.OrdinalIgnoreCase))
    {
        var userId = CurrentUserId(http);
        if (userId is null) return false;
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // image-tests/submissions/{assignmentId:N}/{userId:N}/file.png
        return parts.Length >= 4 && string.Equals(parts[3], userId.Value.ToString("N"), StringComparison.OrdinalIgnoreCase);
    }
    // Fail closed by default. agent-conversations/ must later be checked via ai-api
    // or a shared ACL table; until then it must not be readable by every authenticated user.
    if (normalized.StartsWith("agent-conversations/", StringComparison.OrdinalIgnoreCase)) return false;
    return false;
}

static bool IsEditorOrAdmin(HttpContext http) => http.User?.Identity?.IsAuthenticated == true && TaskForgeRequestSecurity.HasAnyRole(http.User, "Admin", "Editor", "LearningEditor");
static Guid? CurrentUserId(HttpContext http)
{
    var raw = http.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User?.FindFirstValue("sub");
    return Guid.TryParse(raw, out var id) ? id : null;
}

static bool IsPublicUploadFolder(string folder) => NormalizeKey(folder).StartsWith("editor-images/", StringComparison.OrdinalIgnoreCase) || string.Equals(NormalizeKey(folder), "editor-images", StringComparison.OrdinalIgnoreCase) || NormalizeKey(folder).StartsWith("public/", StringComparison.OrdinalIgnoreCase) || string.Equals(NormalizeKey(folder), "public", StringComparison.OrdinalIgnoreCase);
static bool IsPublicFileKey(string key) => NormalizeKey(key).StartsWith("editor-images/", StringComparison.OrdinalIgnoreCase) || NormalizeKey(key).StartsWith("public/", StringComparison.OrdinalIgnoreCase);
static bool IsPrivateFileKey(string key) => NormalizeKey(key).StartsWith("image-tests/", StringComparison.OrdinalIgnoreCase) || NormalizeKey(key).StartsWith("agent-conversations/", StringComparison.OrdinalIgnoreCase);
static string NormalizeKey(string key) => (key ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
static async Task<(bool Ok, string Code, string Stage, string Message, string? Detail)> TryEnsureBucket(IAmazonS3 s3, IConfiguration cfg, bool createIfMissing, CancellationToken ct)
{
    var bucket = Bucket(cfg);
    if (string.IsNullOrWhiteSpace(bucket)) return (false, "S3_BUCKET_NOT_CONFIGURED", "storage.configuration", "Не настроен S3__Bucket для файлового сервиса.", null);
    try
    {
        if (createIfMissing)
        {
            try { await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, ct); }
            catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists" || ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }
        }
        else
        {
            await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 }, ct);
        }
        return (true, "OK", "storage.bucket", "OK", null);
    }
    catch (AmazonS3Exception ex)
    {
        return (false, "MINIO_BUCKET_CHECK_FAILED", "storage.bucket", "Не удалось проверить bucket MinIO. Проверьте S3__Endpoint, S3__Bucket, логин/пароль и доступность MinIO.", ex.Message);
    }
    catch (Exception ex)
    {
        return (false, "MINIO_CONNECTION_FAILED", "storage.connection", "Файловый сервис не смог подключиться к MinIO.", ex.Message);
    }
}
static string Bucket(IConfiguration cfg) => cfg["S3:Bucket"] ?? cfg["S3__Bucket"] ?? "taskforge-files";
static string Required(IConfiguration cfg, string key1, string key2) => cfg[key1] ?? cfg[key2] ?? throw new InvalidOperationException($"Missing configuration value: {key1}/{key2}");
static long MaxUploadBytes(IConfiguration cfg) => cfg.GetValue<long?>("Files:MaxUploadBytes") ?? cfg.GetValue<long?>("Files:MaxImageUploadBytes") ?? 10L * 1024 * 1024;
static string NormalizeFolder(string folder)
{
    var value = NormalizeKey(folder);
    if (value.Contains("..", StringComparison.Ordinal)) return "editor-images";
    var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => new string(part.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray()))
        .Where(part => !string.IsNullOrWhiteSpace(part));
    var normalized = string.Join("/", parts);
    return string.IsNullOrWhiteSpace(normalized) ? "editor-images" : normalized;
}
static string MakeKey(string folder, string extension)
{
    folder = NormalizeFolder(folder);
    extension = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension.Trim();
    if (!extension.StartsWith('.')) extension = "." + extension;
    var file = Guid.NewGuid().ToString("N") + extension.ToLowerInvariant();
    return string.IsNullOrWhiteSpace(folder) ? file : $"{folder}/{file}";
}
static IResult Problem(int status, string code, string stage, string message, string? detail = null) => Results.Json(new { status, code, stage, message, detail, severity = status >= 500 ? "error" : "warning" }, statusCode: status);
public sealed record ImageValidationResult(bool Ok, string? ContentType, string? Extension, string? Code, string? Message);
