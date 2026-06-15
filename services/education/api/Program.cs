using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("education-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "education-api");

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<EducationDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("education-api");

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<EducationDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for EducationDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for EducationDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var ensureScope = app.Services.CreateScope();
    var db = ensureScope.ServiceProvider.GetRequiredService<EducationDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseTaskForgeRequestSecurity("education");

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-education-api" }));
app.MapGet("/health/ready", async (EducationDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect ? Results.Ok(new { status = "ready", service = "taskforge-education-api" }) : Results.StatusCode(503);
});
app.MapGet("/", () => Results.Ok(new { service = "taskforge-education-api", database = "taskforge_education", status = "education microservice active" }));
app.MapGet("/api/education/schema-owner", () => Results.Ok(new { database = "taskforge_education", ownedEntities = new[] { "Course", "Group", "GroupMember" } }));

app.MapGet("/api/courses", async (HttpContext http, EducationDbContext db, IConfiguration cfg, int? page, int? pageSize, string? q, CancellationToken ct) =>
{
    var access = await ResolveAccessContext(http, cfg, db, ct);
    if (!access.UserId.HasValue) return Results.Unauthorized();

    var normalizedQuery = string.Join(' ', (q ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    var requestedPagedShape = page.HasValue || pageSize.HasValue || !string.IsNullOrWhiteSpace(normalizedQuery);
    var size = Math.Clamp(pageSize ?? 12, 1, 50);
    var currentPage = Math.Max(1, page ?? 1);

    var rows = await db.Courses.AsNoTracking().OrderBy(x => x.Title).ToListAsync(ct);
    var visibleRows = rows.Where(x => CanViewCourse(access, x));
    if (!string.IsNullOrWhiteSpace(normalizedQuery))
    {
        visibleRows = visibleRows.Where(x =>
            x.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(x.Description) && x.Description.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)));
    }

    var visible = visibleRows.ToList();
    if (!requestedPagedShape)
    {
        return Results.Ok(visible.Select(x => ToCourseDto(x, CanEditCourse(access, x))).ToList());
    }

    var total = visible.Count;
    var pageRows = visible.Skip((currentPage - 1) * size).Take(size).Select(x => ToCourseDto(x, CanEditCourse(access, x))).ToList();
    return Results.Ok(new PagedResult<CourseDto>(pageRows, currentPage, size, total, currentPage * size < total));
});

app.MapPost("/api/courses", async (CourseRequest request, HttpContext http, IConfiguration cfg, EducationDbContext db) =>
{
    var currentUserId = TaskForgeRequestSecurity.UserId(http, cfg);
    var ownerIds = request.OwnerIds?.Where(x => x != Guid.Empty).Distinct().ToArray();
    if ((ownerIds == null || ownerIds.Length == 0) && currentUserId.HasValue)
    {
        ownerIds = new[] { currentUserId.Value };
    }

    var course = new Course
    {
        Title = string.IsNullOrWhiteSpace(request.Title) ? "Новый курс" : request.Title.Trim(),
        Description = request.Description,
        IsPublic = request.IsPublic ?? false,
        OwnerIdsJson = Serialize(ownerIds),
        VisibleGroupIdsJson = Serialize(request.VisibleGroupIds)
    };
    db.Courses.Add(course);
    await db.SaveChangesAsync();
    return Results.Ok(ToCourseDto(course, canEdit: true));
});

app.MapGet("/api/courses/{id:guid}", async (Guid id, HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
{
    var access = await ResolveAccessContext(http, cfg, db, ct);
    if (!access.UserId.HasValue) return Results.Unauthorized();

    var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    if (course == null || !CanViewCourse(access, course)) return Results.NotFound();

    return Results.Ok(ToCourseDto(course, CanEditCourse(access, course)));
});

app.MapPut("/api/courses/{id:guid}", async (Guid id, CourseRequest request, EducationDbContext db) =>
{
    var course = await db.Courses.FindAsync(id);
    if (course == null) return Results.NotFound();
    if (!string.IsNullOrWhiteSpace(request.Title)) course.Title = request.Title.Trim();
    course.Description = request.Description;
    if (request.IsPublic.HasValue) course.IsPublic = request.IsPublic.Value;
    if (request.OwnerIds != null) course.OwnerIdsJson = Serialize(request.OwnerIds);
    if (request.VisibleGroupIds != null) course.VisibleGroupIdsJson = Serialize(request.VisibleGroupIds);
    course.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToCourseDto(course, canEdit: true));
});

app.MapDelete("/api/courses/{id:guid}", async (Guid id, EducationDbContext db) =>
{
    var course = await db.Courses.FindAsync(id);
    if (course == null) return Results.NotFound();
    db.Courses.Remove(course);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "deleted" });
});

app.MapGet("/api/admin/users/{userId:guid}/groups", async (Guid userId, EducationDbContext db) => Results.Ok(await db.GroupMembers.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.GroupId).ToListAsync()));

app.MapPost("/api/internal/courses/metadata", async (CourseIdsRequest request, EducationDbContext db, IDistributedCache cache, IConfiguration cfg, ILogger<Program> logger, CancellationToken ct) =>
{
    var ids = (request.CourseIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().Take(1000).OrderBy(x => x).ToArray();
    if (ids.Length == 0) return Results.Ok(Array.Empty<CourseMetadataDto>());
    var key = TaskForgeCache.Key("education:course-metadata:v2", ids);
    var rows = await TaskForgeCache.GetOrSetAsync(cache, cfg, logger, key, TaskForgeCache.Ttl(cfg, "Metadata", 300), async token =>
    {
        var courses = await db.Courses.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(token);
        return courses.Select(x => new CourseMetadataDto(x.Id, x.Id, x.Title, x.Title, x.Description, x.IsPublic)).ToList();
    }, ct);
    return Results.Ok(rows);
});

app.MapGet("/api/internal/groups/{groupId:guid}/members", async (Guid groupId, EducationDbContext db, CancellationToken ct) =>
{
    var ids = await db.GroupMembers.AsNoTracking().Where(x => x.GroupId == groupId).Select(x => x.UserId).ToListAsync(ct);
    return Results.Ok(new { groupId, userIds = ids });
});

app.MapGet("/api/internal/users/{userId:guid}/groups", async (Guid userId, EducationDbContext db, CancellationToken ct) =>
{
    var ids = await db.GroupMembers.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.GroupId).ToListAsync(ct);
    return Results.Ok(new { userId, groupIds = ids });
});

app.MapGet("/api/internal/courses/{courseId:guid}/access/{userId:guid}", async (Guid courseId, Guid userId, EducationDbContext db, CancellationToken ct) =>
{
    var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == courseId, ct);
    if (course == null) return Results.NotFound();

    var groupIds = await db.GroupMembers.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.GroupId).ToListAsync(ct);
    var access = new EducationAccessContext(userId, false, groupIds.ToHashSet());
    return Results.Ok(new { courseId, userId, canView = CanViewCourse(access, course), canEdit = CanEditCourse(access, course), isPublic = course.IsPublic });
});

app.MapPost("/api/courses/{courseId:guid}/visible-groups", async (Guid courseId, CourseGroupsRequest request, EducationDbContext db) =>
{
    var course = await db.Courses.FindAsync(courseId);
    if (course == null) return Results.NotFound();
    course.VisibleGroupIdsJson = Serialize(request.GroupIds);
    course.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToCourseDto(course, canEdit: true));
});

app.MapPost("/api/courses/{courseId:guid}/owners", async (Guid courseId, CourseOwnersRequest request, EducationDbContext db) =>
{
    var course = await db.Courses.FindAsync(courseId);
    if (course == null) return Results.NotFound();
    course.OwnerIdsJson = Serialize(request.OwnerIds);
    course.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToCourseDto(course, canEdit: true));
});

app.MapGet("/api/groups", async (HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
{
    var access = await ResolveAccessContext(http, cfg, db, ct);
    if (!access.UserId.HasValue) return Results.Unauthorized();

    var query = db.Groups.AsNoTracking().OrderBy(x => x.Name);
    if (!access.IsEditorOrAdmin)
    {
        query = query.Where(x => access.GroupIds.Contains(x.Id)).OrderBy(x => x.Name);
    }

    var rows = await query.ToListAsync(ct);
    return Results.Ok(rows.Select(x => ToGroupDto(x, showCode: access.IsEditorOrAdmin)).ToList());
});
app.MapGet("/api/admin/groups", async (EducationDbContext db) => Results.Ok((await db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync()).Select(x => ToGroupDto(x, showCode: true)).ToList()));

app.MapPost("/api/admin/groups", async (GroupRequest request, EducationDbContext db) =>
{
    var group = new Group { Name = string.IsNullOrWhiteSpace(request.Name) ? "Новая группа" : request.Name.Trim(), Code = request.Code, IsActive = request.IsActive ?? true };
    db.Groups.Add(group);
    await db.SaveChangesAsync();
    return Results.Ok(ToGroupDto(group, showCode: true));
});

app.MapPut("/api/admin/groups/{id:guid}", async (Guid id, GroupRequest request, EducationDbContext db) =>
{
    var group = await db.Groups.FindAsync(id);
    if (group == null) return Results.NotFound();
    if (!string.IsNullOrWhiteSpace(request.Name)) group.Name = request.Name.Trim();
    group.Code = request.Code;
    if (request.IsActive.HasValue) group.IsActive = request.IsActive.Value;
    await db.SaveChangesAsync();
    return Results.Ok(ToGroupDto(group, showCode: true));
});

app.MapDelete("/api/admin/groups/{id:guid}", async (Guid id, EducationDbContext db) =>
{
    var group = await db.Groups.FindAsync(id);
    if (group == null) return Results.NotFound();
    db.Groups.Remove(group);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "deleted" });
});

app.MapPost("/api/admin/groups/{groupId:guid}/members", async (Guid groupId, GroupMemberRequest request, EducationDbContext db) =>
{
    if (!await db.GroupMembers.AnyAsync(x => x.GroupId == groupId && x.UserId == request.UserId))
    {
        db.GroupMembers.Add(new GroupMember { GroupId = groupId, UserId = request.UserId });
        await db.SaveChangesAsync();
    }
    return Results.Ok(new { groupId, request.UserId });
});

app.MapDelete("/api/admin/groups/{groupId:guid}/members/{userId:guid}", async (Guid groupId, Guid userId, EducationDbContext db) =>
{
    var rows = await db.GroupMembers.Where(x => x.GroupId == groupId && x.UserId == userId).ToListAsync();
    db.GroupMembers.RemoveRange(rows);
    await db.SaveChangesAsync();
    return Results.Ok(new { groupId, userId });
});

app.Run();

static async Task<EducationAccessContext> ResolveAccessContext(HttpContext http, IConfiguration cfg, EducationDbContext db, CancellationToken ct)
{
    var principal = http.User?.Identity?.IsAuthenticated == true ? http.User : TaskForgeRequestSecurity.ValidateUser(http, cfg);
    var isEditor = principal is not null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin", "Editor", "LearningEditor");
    var userId = TaskForgeRequestSecurity.UserId(http, cfg);
    var groupIds = userId.HasValue
        ? await db.GroupMembers.AsNoTracking().Where(x => x.UserId == userId.Value).Select(x => x.GroupId).ToListAsync(ct)
        : new List<Guid>();
    return new EducationAccessContext(userId, isEditor, groupIds.ToHashSet());
}

static bool CanViewCourse(EducationAccessContext access, Course course)
{
    if (access.IsEditorOrAdmin) return true;
    if (course.IsPublic) return true;
    if (access.UserId.HasValue && DeserializeIds(course.OwnerIdsJson).Contains(access.UserId.Value)) return true;
    var visibleGroups = DeserializeIds(course.VisibleGroupIdsJson);
    return visibleGroups.Length > 0 && visibleGroups.Any(access.GroupIds.Contains);
}

static bool CanEditCourse(EducationAccessContext access, Course course)
{
    if (access.IsEditorOrAdmin) return true;
    return access.UserId.HasValue && DeserializeIds(course.OwnerIdsJson).Contains(access.UserId.Value);
}

static string Serialize(IEnumerable<Guid>? ids) => JsonSerializer.Serialize((ids ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().ToArray());
static Guid[] DeserializeIds(string? json)
{
    try { return string.IsNullOrWhiteSpace(json) ? Array.Empty<Guid>() : JsonSerializer.Deserialize<Guid[]>(json) ?? Array.Empty<Guid>(); }
    catch { return Array.Empty<Guid>(); }
}
static CourseDto ToCourseDto(Course c, bool canEdit)
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
static object ToGroupDto(Group g, bool showCode) => new { g.Id, g.Name, code = showCode ? g.Code ?? string.Empty : string.Empty, g.IsActive, g.CreatedAt };

public sealed record EducationAccessContext(Guid? UserId, bool IsEditorOrAdmin, HashSet<Guid> GroupIds);
public sealed record CourseIdsRequest(Guid[]? CourseIds);
public sealed record CourseGroupsRequest(Guid[]? GroupIds);
public sealed record CourseOwnersRequest(Guid[]? OwnerIds);
public sealed record CourseRequest(string? Title, string? Description, bool? IsPublic, Guid[]? VisibleGroupIds, Guid[]? OwnerIds);
public sealed record GroupRequest(string? Name, string? Code, bool? IsActive);
public sealed record GroupMemberRequest(Guid UserId);

public sealed record CourseDto(Guid Id, string Title, string? Description, bool IsPublic, Guid[] VisibleGroupIds, Guid[] OwnerIds, bool CanEdit, bool IsCompletedForCurrentUser, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CourseMetadataDto(Guid Id, Guid CourseId, string Title, string CourseTitle, string? Description, bool IsPublic);
public sealed record PagedResult<T>(List<T> Items, int Page, int PageSize, int Total, bool HasMore);
