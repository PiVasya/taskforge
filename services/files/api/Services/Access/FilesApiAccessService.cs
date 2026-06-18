using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TaskForge.Files.Api.Data;
using TaskForge.Files.Api.Domain;

using TaskForge.Files.Api.Contracts;
using static TaskForge.Files.Api.Services.Common.FilesApiCommonService;
using static TaskForge.Files.Api.Services.Image.FilesApiImageService;
using static TaskForge.Files.Api.Services.Mapping.FilesApiMappingService;
using static TaskForge.Files.Api.Services.Serialization.FilesApiSerializationService;

namespace TaskForge.Files.Api.Services.Access;

internal static class FilesApiAccessService
{
    internal static bool IsEditorOrAdmin(HttpContext http) => http.User?.Identity?.IsAuthenticated == true && TaskForgeRequestSecurity.HasAnyRole(http.User, "Admin", "Editor", "LearningEditor");

    internal static Guid? CurrentUserId(HttpContext http)
    {
        var raw = http.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User?.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : null;
    }

}
