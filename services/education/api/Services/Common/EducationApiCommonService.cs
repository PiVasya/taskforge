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
    internal static bool CanViewCourse(EducationAccessContext access, Course course, bool blockedByHierarchy = false)
    {
        if (access.IsEditorOrAdmin) return true;
        if (blockedByHierarchy) return false;
        return CanViewCourseDirect(access, course);
    }

    internal static bool CanViewCourseWithAncestors(EducationAccessContext access, Course course, IReadOnlyDictionary<Guid, Course> byId)
    {
        if (access.IsEditorOrAdmin) return true;
        var current = course;
        var seen = new HashSet<Guid>();
        while (seen.Add(current.Id))
        {
            if (!CanViewCourseDirect(access, current)) return false;
            if (!current.ParentCourseId.HasValue) return true;
            if (!byId.TryGetValue(current.ParentCourseId.Value, out var parent)) return false;
            current = parent;
        }
        return false;
    }

    internal static HashSet<Guid> BuildUnavailableCourseIds(EducationAccessContext access, IEnumerable<Course> courses)
    {
        if (access.IsEditorOrAdmin) return new HashSet<Guid>();
        var rows = courses.ToList();
        var byId = rows.ToDictionary(x => x.Id);
        return rows
            .Where(course => !CanViewCourseWithAncestors(access, course, byId))
            .Select(course => course.Id)
            .ToHashSet();
    }

    internal static bool CanEditCourse(EducationAccessContext access, Course course)
    {
        if (access.IsEditorOrAdmin) return true;
        return access.UserId.HasValue && DeserializeIds(course.OwnerIdsJson).Contains(access.UserId.Value);
    }

    internal static void NormalizeCourseAudience(Course course)
    {
        if (course.IsHiddenFromStudents)
        {
            course.IsPublic = false;
            return;
        }
        if (course.IsPublic) course.IsHiddenFromStudents = false;
    }

    private static bool CanViewCourseDirect(EducationAccessContext access, Course course)
    {
        if (course.IsHiddenFromStudents) return false;
        if (course.IsPublic) return true;
        if (access.UserId.HasValue && DeserializeIds(course.OwnerIdsJson).Contains(access.UserId.Value)) return true;
        var visibleGroups = DeserializeIds(course.VisibleGroupIdsJson);
        return visibleGroups.Length > 0 && visibleGroups.Any(access.GroupIds.Contains);
    }
}
