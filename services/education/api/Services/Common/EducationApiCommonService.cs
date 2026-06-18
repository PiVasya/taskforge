using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;

using TaskForge.Education.Api.Contracts;
using static TaskForge.Education.Api.Services.Access.EducationApiAccessService;
using static TaskForge.Education.Api.Services.Mapping.EducationApiMappingService;
using static TaskForge.Education.Api.Services.Serialization.EducationApiSerializationService;

namespace TaskForge.Education.Api.Services.Common;

internal static class EducationApiCommonService
{
    internal static bool CanViewCourse(EducationAccessContext access, Course course)
    {
        if (access.IsEditorOrAdmin) return true;
        if (course.IsPublic) return true;
        if (access.UserId.HasValue && DeserializeIds(course.OwnerIdsJson).Contains(access.UserId.Value)) return true;
        var visibleGroups = DeserializeIds(course.VisibleGroupIdsJson);
        return visibleGroups.Length > 0 && visibleGroups.Any(access.GroupIds.Contains);
    }

    internal static bool CanEditCourse(EducationAccessContext access, Course course)
    {
        if (access.IsEditorOrAdmin) return true;
        return access.UserId.HasValue && DeserializeIds(course.OwnerIdsJson).Contains(access.UserId.Value);
    }

}
