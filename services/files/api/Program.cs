using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using TaskForge.Files.Api.Data;
using TaskForge.Files.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("files-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "files-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
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
    if (!IsAllowedImage(file)) return Problem(400, "UNSUPPORTED_FILE_TYPE", "request.validation", "Можно загружать только изображения PNG, JPEG, WEBP, GIF или SVG.", file.ContentType);

    var bucket = Bucket(cfg);
    var folder = (form.TryGetValue("folder", out var f) ? f.ToString() : null) ?? "editor-images";
    var key = MakeKey(folder, Path.GetExtension(file.FileName));

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
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
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
        ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
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
        url = $"/api/files/{Uri.EscapeDataString(key)}",
        privateUrl = $"/api/private-files/{Uri.EscapeDataString(key)}",
        storage = "minio"
    });
}).DisableAntiforgery();

app.MapGet("/api/files", async (FilesDbContext db) => Results.Ok((await db.Files.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync()).Select(ToDto)));
app.MapGet("/api/files/{**key}", async (string key, IAmazonS3 s3, IConfiguration cfg, CancellationToken ct) => await Download(key, s3, cfg, isPrivate: false, ct));
app.MapGet("/api/private-files/{**key}", async (string key, IAmazonS3 s3, IConfiguration cfg, CancellationToken ct) => await Download(key, s3, cfg, isPrivate: true, ct));

app.Run();

static object ToDto(StoredFile item) => new { item.Id, item.FileName, item.ContentType, item.Size, key = item.Url, url = $"/api/files/{Uri.EscapeDataString(item.Url)}", privateUrl = $"/api/private-files/{Uri.EscapeDataString(item.Url)}", item.CreatedAt };
static bool IsAllowedImage(IFormFile file)
{
    var ct = (file.ContentType ?? string.Empty).ToLowerInvariant();
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    return ct.StartsWith("image/") || ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".svg";
}
static async Task<IResult> Download(string key, IAmazonS3 s3, IConfiguration cfg, bool isPrivate, CancellationToken ct)
{
    var decoded = Uri.UnescapeDataString(key ?? string.Empty).TrimStart('/');
    if (string.IsNullOrWhiteSpace(decoded)) return Problem(400, "FILE_KEY_REQUIRED", "request.validation", "Не указан ключ файла.");
    var ensure = await TryEnsureBucket(s3, cfg, createIfMissing: true, ct);
    if (!ensure.Ok) return Problem(503, ensure.Code, ensure.Stage, ensure.Message, ensure.Detail);
    try
    {
        var resp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket(cfg), Key = decoded }, ct);
        var fileName = Path.GetFileName(decoded);
        return Results.File(resp.ResponseStream, string.IsNullOrWhiteSpace(resp.Headers.ContentType) ? "application/octet-stream" : resp.Headers.ContentType, fileName, enableRangeProcessing: true);
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Problem(404, "FILE_NOT_FOUND", isPrivate ? "storage.get_private_object" : "storage.get_public_object", "Файл не найден в MinIO. Возможно, объект был удалён или ссылка устарела.", decoded);
    }
    catch (AmazonS3Exception ex)
    {
        return Problem(503, "MINIO_GET_OBJECT_FAILED", isPrivate ? "storage.get_private_object" : "storage.get_public_object", "Не удалось получить файл из MinIO. Проверьте bucket, права доступа и состояние MinIO.", ex.Message);
    }
    catch (Exception ex)
    {
        return Problem(503, "FILE_DOWNLOAD_FAILED", isPrivate ? "storage.get_private_object" : "storage.get_public_object", "Не удалось скачать файл из хранилища.", ex.Message);
    }
}
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
static string MakeKey(string folder, string extension)
{
    folder = (folder ?? string.Empty).Trim().Trim('/');
    extension = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension.Trim();
    if (!extension.StartsWith('.')) extension = "." + extension;
    var file = Guid.NewGuid().ToString("N") + extension.ToLowerInvariant();
    return string.IsNullOrWhiteSpace(folder) ? file : $"{folder}/{file}";
}
static IResult Problem(int status, string code, string stage, string message, string? detail = null) => Results.Json(new { status, code, stage, message, detail, severity = status >= 500 ? "error" : "warning" }, statusCode: status);
