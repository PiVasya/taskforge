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
using static TaskForge.Files.Api.Services.Image.FilesApiImageService;
using static TaskForge.Files.Api.Services.Mapping.FilesApiMappingService;
using static TaskForge.Files.Api.Services.Serialization.FilesApiSerializationService;

namespace TaskForge.Files.Api.Endpoints;

internal static partial class FilesApiEndpoints
{
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-files-api" }));

        app.MapGet("/health/ready", async (FilesDbContext db, IAmazonS3 s3, IConfiguration cfg) =>
        {
            if (!await db.Database.CanConnectAsync()) return Results.Json(new { status = 503, service = "taskforge-files-api", stage = "database.connect", code = "DB_NOT_READY", message = "files-api не может подключиться к PostgreSQL." }, statusCode: 503);
            var bucketCheck = await TryEnsureBucket(s3, cfg, createIfMissing: true, default);
            return bucketCheck.Ok ? Results.Ok(new { status = "ready", service = "taskforge-files-api", storage = "minio" }) : Results.Json(new { status = 503, service = "taskforge-files-api", storage = "minio", bucketCheck.Stage, bucketCheck.Code, bucketCheck.Message, bucketCheck.Detail }, statusCode: 503);
        });

        app.MapGet("/", () => Results.Ok(new { service = "taskforge-files-api", database = "taskforge_files", storage = "minio", status = "files microservice active" }));

        app.MapGet("/api/files/schema-owner", () => Results.Ok(new { database = "taskforge_files", ownedEntities = new[] { "StoredFile" }, storage = "MinIO/S3" }));

        return app;
    }
}
