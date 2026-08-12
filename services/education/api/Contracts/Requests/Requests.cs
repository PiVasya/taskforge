using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;


namespace TaskForge.Education.Api.Contracts;

public sealed record CourseIdsRequest(Guid[]? CourseIds);

public sealed record CourseGraphImportItemRequest(string Key, Guid? Id, string? Title);

public sealed record CourseGraphImportEnsureRequest(Guid RootCourseId, Guid? OwnerId, CourseGraphImportItemRequest[]? Courses);

public sealed record CourseAccessBatchRequest(Guid UserId, Guid[]? CourseIds, bool BypassStudentVisibility = false, bool IncludeProgressionRules = true);

public sealed record CourseGroupsRequest(Guid[]? GroupIds);

public sealed record CourseOwnersRequest(Guid[]? OwnerIds);

public sealed record CourseRequest(
    string? Title,
    string? Description,
    bool? IsPublic,
    Guid[]? VisibleGroupIds,
    Guid[]? OwnerIds,
    int? Sort,
    Guid? ParentCourseId,
    bool? IsHiddenFromStudents);

public sealed record CourseSortRequest(int Sort);

public sealed record CoursePositionRequest(Guid? ParentCourseId, int? Position);

public sealed record CourseMapSaveRequest(int ExpectedVersion, JsonElement Document);

public sealed record GroupRequest(string? Name, string? Code, bool? IsActive);

public sealed record GroupMemberRequest(Guid UserId);
