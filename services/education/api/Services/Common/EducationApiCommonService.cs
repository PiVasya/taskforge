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
    internal static bool CanViewCourse(EducationAccessContext access, Course course, bool hiddenByHierarchy = false)
    {
        if (access.IsEditorOrAdmin) return true;
        if (hiddenByHierarchy || course.IsHiddenFromStudents) return false;
        if (course.IsPublic) return true;
        if (access.UserId.HasValue && DeserializeIds(course.OwnerIdsJson).Contains(access.UserId.Value)) return true;
        var visibleGroups = DeserializeIds(course.VisibleGroupIdsJson);
        return visibleGroups.Length > 0 && visibleGroups.Any(access.GroupIds.Contains);
    }

    internal static bool IsHiddenByHierarchy(Course course, IReadOnlyDictionary<Guid, Course> byId)
    {
        var current = course;
        var seen = new HashSet<Guid>();
        while (seen.Add(current.Id))
        {
            if (current.IsHiddenFromStudents) return true;
            if (!current.ParentCourseId.HasValue || !byId.TryGetValue(current.ParentCourseId.Value, out var parent)) return false;
            current = parent;
        }
        return true;
    }

    internal static HashSet<Guid> BuildHiddenCourseIds(IEnumerable<Course> courses)
    {
        var rows = courses.ToList();
        var byId = rows.ToDictionary(x => x.Id);
        var hidden = new HashSet<Guid>();
        foreach (var course in rows)
        {
            var current = course;
            var seen = new HashSet<Guid>();
            while (true)
            {
                if (!seen.Add(current.Id)) break;
                if (current.IsHiddenFromStudents)
                {
                    hidden.Add(course.Id);
                    break;
                }
                if (!current.ParentCourseId.HasValue || !byId.TryGetValue(current.ParentCourseId.Value, out var parent)) break;
                current = parent;
            }
        }
        return hidden;
    }

    internal static bool CanEditCourse(EducationAccessContext access, Course course)
    {
        if (access.IsEditorOrAdmin) return true;
        return access.UserId.HasValue && DeserializeIds(course.OwnerIdsJson).Contains(access.UserId.Value);
    }

}
