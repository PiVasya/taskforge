using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TaskForge.Files.Api.Data;
using TaskForge.Files.Api.Domain;

using TaskForge.Files.Api.Contracts;
using static TaskForge.Files.Api.Services.Access.FilesApiAccessService;
using static TaskForge.Files.Api.Services.Image.FilesApiImageService;
using static TaskForge.Files.Api.Services.Mapping.FilesApiMappingService;
using static TaskForge.Files.Api.Services.Serialization.FilesApiSerializationService;

namespace TaskForge.Files.Api.Services.Common;

internal static class FilesApiCommonService
{
    internal static async Task<(bool Ok, string Code, string Stage, string Message, string? Detail)> TryEnsureBucket(IAmazonS3 s3, IConfiguration cfg, bool createIfMissing, CancellationToken ct)
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

    internal static string Bucket(IConfiguration cfg) => cfg["S3:Bucket"] ?? cfg["S3__Bucket"] ?? "taskforge-files";

    internal static string Required(IConfiguration cfg, string key1, string key2) => cfg[key1] ?? cfg[key2] ?? throw new InvalidOperationException($"Missing configuration value: {key1}/{key2}");

    internal static string MakeKey(string folder, string extension)
    {
        folder = NormalizeFolder(folder);
        extension = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension.Trim();
        if (!extension.StartsWith('.')) extension = "." + extension;
        var file = Guid.NewGuid().ToString("N") + extension.ToLowerInvariant();
        return string.IsNullOrWhiteSpace(folder) ? file : $"{folder}/{file}";
    }

    internal static IResult Problem(int status, string code, string stage, string message, string? detail = null) => Microsoft.AspNetCore.Http.Results.Json(new { status, code, stage, message, detail, severity = status >= 500 ? "error" : "warning" }, statusCode: status);

}
