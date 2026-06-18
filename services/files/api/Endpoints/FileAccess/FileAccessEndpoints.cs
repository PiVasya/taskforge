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
    private static WebApplication MapFileAccessEndpoints(WebApplication app)
    {
        app.MapGet("/api/files", async (HttpContext http, FilesDbContext db) =>
        {
            if (!IsEditorOrAdmin(http)) return Microsoft.AspNetCore.Http.Results.Json(new { status = 403, code = "EDITOR_REQUIRED", message = "Список файлов доступен только редактору или администратору." }, statusCode: StatusCodes.Status403Forbidden);
            var rows = await db.Files.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(ToDto));
        });

        app.MapGet("/api/files/{**key}", async (string key, HttpContext http, IAmazonS3 s3, IConfiguration cfg, CancellationToken ct) => await Download(key, http, s3, cfg, publicRoute: true, internalRoute: false, ct));

        app.MapGet("/api/internal/files/{**key}", async (string key, HttpContext http, IAmazonS3 s3, IConfiguration cfg, CancellationToken ct) => await Download(key, http, s3, cfg, publicRoute: false, internalRoute: true, ct));

        app.MapGet("/api/private-files/{**key}", async (string key, HttpContext http, IAmazonS3 s3, IConfiguration cfg, CancellationToken ct) => await Download(key, http, s3, cfg, publicRoute: false, internalRoute: false, ct));

        return app;
    }
}
