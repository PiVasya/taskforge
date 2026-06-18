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
    private static WebApplication MapImagesEndpoints(WebApplication app)
    {
        app.MapPost("/api/files/images", async (HttpRequest req, FilesDbContext db, IAmazonS3 s3, IConfiguration cfg, ILoggerFactory loggerFactory, CancellationToken ct) =>
            await UploadImageFromRequest(req, db, s3, cfg, loggerFactory, false, ct)).DisableAntiforgery();

        app.MapPost("/api/internal/files/images", async (HttpRequest req, FilesDbContext db, IAmazonS3 s3, IConfiguration cfg, ILoggerFactory loggerFactory, CancellationToken ct) =>
            await UploadImageFromRequest(req, db, s3, cfg, loggerFactory, true, ct)).DisableAntiforgery();

        return app;
    }
}
