using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;


namespace TaskForge.Education.Api.Contracts;

public sealed record EducationAccessContext(Guid? UserId, bool IsEditorOrAdmin, HashSet<Guid> GroupIds);

public sealed record CourseDto(Guid Id, string Title, string? Description, bool IsPublic, Guid[] VisibleGroupIds, Guid[] OwnerIds, bool CanEdit, bool IsCompletedForCurrentUser, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? ParentCourseId, int Sort);

public sealed record CourseMetadataDto(Guid Id, Guid CourseId, string Title, string CourseTitle, string? Description, bool IsPublic);

public sealed record CourseTreeCourseDto(Guid Id, Guid? ParentCourseId, string Title, string? Description, bool IsPublic, int Sort);

public sealed record CourseTreeResponse(Guid CourseId, Guid[] CourseIds, List<CourseTreeCourseDto> Courses);

public sealed record PagedResult<T>(List<T> Items, int Page, int PageSize, int Total, bool HasMore);
