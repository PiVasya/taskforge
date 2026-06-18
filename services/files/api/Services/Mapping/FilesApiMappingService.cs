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
using static TaskForge.Files.Api.Services.Serialization.FilesApiSerializationService;

namespace TaskForge.Files.Api.Services.Mapping;

internal static class FilesApiMappingService
{
    internal static object ToDto(StoredFile item)
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

}
