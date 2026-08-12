using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;


namespace TaskForge.Education.Api.Contracts;

public sealed record EducationAccessContext(Guid? UserId, bool IsEditorOrAdmin, HashSet<Guid> GroupIds);

public sealed record CourseDto(Guid Id, string Title, string? Description, bool IsPublic, bool IsHiddenFromStudents, Guid[] VisibleGroupIds, Guid[] OwnerIds, bool CanEdit, bool IsCompletedForCurrentUser, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? ParentCourseId, int Sort);

public sealed record CourseMetadataDto(Guid Id, Guid CourseId, string Title, string CourseTitle, string? Description, bool IsPublic);

public sealed record CourseGraphImportItemResponse(string Key, Guid Id, string Title, bool Created);

public sealed record CourseGraphImportEnsureResponse(List<CourseGraphImportItemResponse> Courses);

public sealed record CourseAccessDto(Guid CourseId, Guid UserId, bool CanView, bool CanEdit, bool IsPublic, Guid RootCourseId, bool HasProgressionRules);

public sealed record CourseTreeCourseDto(Guid Id, Guid? ParentCourseId, string Title, string? Description, bool IsPublic, bool IsHiddenFromStudents, int Sort);

public sealed record CourseTreeResponse(Guid CourseId, Guid[] CourseIds, List<CourseTreeCourseDto> Courses);

public sealed record CourseMapResponse(Guid RootCourseId, Guid RequestedCourseId, int Version, JsonElement? Document, DateTimeOffset? UpdatedAt, Guid? UpdatedBy);

public sealed record CourseMapMetaResponse(Guid RootCourseId, Guid RequestedCourseId, int Version, DateTimeOffset? UpdatedAt, Guid? UpdatedBy);

public sealed record CourseMapPresenceDto(Guid UserId, string DisplayName, string? AvatarUrl, bool IsDirty, DateTimeOffset LastSeenAt);

public sealed record PagedResult<T>(List<T> Items, int Page, int PageSize, int Total, bool HasMore);
