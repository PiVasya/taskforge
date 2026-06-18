using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;

using TaskForge.Education.Api.Contracts;
using static TaskForge.Education.Api.Services.Access.EducationApiAccessService;
using static TaskForge.Education.Api.Services.Common.EducationApiCommonService;
using static TaskForge.Education.Api.Services.Serialization.EducationApiSerializationService;

namespace TaskForge.Education.Api.Services.Mapping;

internal static class EducationApiMappingService
{
    internal static CourseDto ToCourseDto(Course c, bool canEdit)
    {
        var visibleGroupIds = canEdit ? DeserializeIds(c.VisibleGroupIdsJson) : Array.Empty<Guid>();
        var ownerIds = canEdit ? DeserializeIds(c.OwnerIdsJson) : Array.Empty<Guid>();
        return new CourseDto(
            c.Id,
            c.Title,
            c.Description,
            c.IsPublic,
            visibleGroupIds,
            ownerIds,
            canEdit,
            false,
            c.CreatedAt,
            c.UpdatedAt);
    }

    internal static object ToGroupDto(Group g, bool showCode) => new { g.Id, g.Name, code = showCode ? g.Code ?? string.Empty : string.Empty, g.IsActive, g.CreatedAt };

}
