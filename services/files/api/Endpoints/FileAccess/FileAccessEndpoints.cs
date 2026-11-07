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
            var prefix = http.Request.Query["prefix"].ToString();
            var query = db.Files.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                // Scoped public editor images only. Do not expose private judge files
                // through the course image gallery or permit arbitrary SQL wildcards.
                var clean = NormalizeKey(prefix);
                var segments = clean.Split('/');
                if (segments.Length != 3 || !string.Equals(segments[0], "editor-images", StringComparison.Ordinal) ||
                    !string.Equals(segments[1], "courses", StringComparison.Ordinal) ||
                    !Guid.TryParse(segments[2], out _) || !prefix.EndsWith("/", StringComparison.Ordinal))
                    return Microsoft.AspNetCore.Http.Results.Json(new { code = "INVALID_IMAGE_PREFIX" }, statusCode: StatusCodes.Status400BadRequest);
                // Use the separating slash so a prefix cannot match another course.
                query = query.Where(x => x.Url.StartsWith(clean + "/"));
            }
            var offset = int.TryParse(http.Request.Query["offset"], out var requestedOffset) ? Math.Clamp(requestedOffset, 0, 10000) : 0;
            var limit = int.TryParse(http.Request.Query["limit"], out var requestedLimit) ? Math.Clamp(requestedLimit, 1, 200) : 100;
            var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Skip(offset).Take(limit).ToListAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(ToDto));
        });

        app.MapGet("/api/files/{**key}", async (string key, HttpContext http, IAmazonS3 s3, IConfiguration cfg, CancellationToken ct) => await Download(key, http, s3, cfg, publicRoute: true, internalRoute: false, ct));

        app.MapGet("/api/internal/files/{**key}", async (string key, HttpContext http, IAmazonS3 s3, IConfiguration cfg, CancellationToken ct) => await Download(key, http, s3, cfg, publicRoute: false, internalRoute: true, ct));

        app.MapGet("/api/private-files/{**key}", async (string key, HttpContext http, IAmazonS3 s3, IConfiguration cfg, CancellationToken ct) => await Download(key, http, s3, cfg, publicRoute: false, internalRoute: false, ct));

        return app;
    }
}
