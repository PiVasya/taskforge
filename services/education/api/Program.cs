using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<EducationDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

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

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-education-api" }));
app.MapGet("/health/ready", async (EducationDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect ? Results.Ok(new { status = "ready", service = "taskforge-education-api" }) : Results.StatusCode(503);
});
app.MapGet("/", () => Results.Ok(new { service = "taskforge-education-api", database = "taskforge_education", status = "education microservice active" }));
app.MapGet("/api/education/schema-owner", () => Results.Ok(new { database = "taskforge_education", ownedEntities = new[] { "Course", "Group", "GroupMember" } }));

app.MapGet("/api/courses", async (EducationDbContext db) =>
{
    var rows = await db.Courses.AsNoTracking().OrderBy(x => x.Title).ToListAsync();
    return Results.Ok(rows.Select(ToCourseDto).ToList());
});

app.MapPost("/api/courses", async (CourseRequest request, EducationDbContext db) =>
{
    var course = new Course
    {
        Title = string.IsNullOrWhiteSpace(request.Title) ? "Новый курс" : request.Title.Trim(),
        Description = request.Description,
        IsPublic = request.IsPublic ?? false,
        OwnerIdsJson = Serialize(request.OwnerIds),
        VisibleGroupIdsJson = Serialize(request.VisibleGroupIds)
    };
    db.Courses.Add(course);
    await db.SaveChangesAsync();
    return Results.Ok(ToCourseDto(course));
});

app.MapGet("/api/courses/{id:guid}", async (Guid id, EducationDbContext db) =>
{
    var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    return course == null ? Results.NotFound() : Results.Ok(ToCourseDto(course));
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
    return Results.Ok(ToCourseDto(course));
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

app.MapPost("/api/courses/{courseId:guid}/visible-groups", async (Guid courseId, CourseGroupsRequest request, EducationDbContext db) =>
{
    var course = await db.Courses.FindAsync(courseId);
    if (course == null) return Results.NotFound();
    course.VisibleGroupIdsJson = Serialize(request.GroupIds);
    course.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToCourseDto(course));
});

app.MapPost("/api/courses/{courseId:guid}/owners", async (Guid courseId, CourseOwnersRequest request, EducationDbContext db) =>
{
    var course = await db.Courses.FindAsync(courseId);
    if (course == null) return Results.NotFound();
    course.OwnerIdsJson = Serialize(request.OwnerIds);
    course.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToCourseDto(course));
});

app.MapGet("/api/groups", async (EducationDbContext db) => Results.Ok((await db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync()).Select(ToGroupDto).ToList()));
app.MapGet("/api/admin/groups", async (EducationDbContext db) => Results.Ok((await db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync()).Select(ToGroupDto).ToList()));

app.MapPost("/api/admin/groups", async (GroupRequest request, EducationDbContext db) =>
{
    var group = new Group { Name = string.IsNullOrWhiteSpace(request.Name) ? "Новая группа" : request.Name.Trim(), Code = request.Code, IsActive = request.IsActive ?? true };
    db.Groups.Add(group);
    await db.SaveChangesAsync();
    return Results.Ok(ToGroupDto(group));
});

app.MapPut("/api/admin/groups/{id:guid}", async (Guid id, GroupRequest request, EducationDbContext db) =>
{
    var group = await db.Groups.FindAsync(id);
    if (group == null) return Results.NotFound();
    if (!string.IsNullOrWhiteSpace(request.Name)) group.Name = request.Name.Trim();
    group.Code = request.Code;
    if (request.IsActive.HasValue) group.IsActive = request.IsActive.Value;
    await db.SaveChangesAsync();
    return Results.Ok(ToGroupDto(group));
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

static string Serialize(IEnumerable<Guid>? ids) => JsonSerializer.Serialize(ids ?? Array.Empty<Guid>());
static Guid[] DeserializeIds(string? json)
{
    try { return string.IsNullOrWhiteSpace(json) ? Array.Empty<Guid>() : JsonSerializer.Deserialize<Guid[]>(json) ?? Array.Empty<Guid>(); }
    catch { return Array.Empty<Guid>(); }
}
static object ToCourseDto(Course c) => new
{
    c.Id,
    c.Title,
    c.Description,
    c.IsPublic,
    visibleGroupIds = DeserializeIds(c.VisibleGroupIdsJson),
    ownerIds = DeserializeIds(c.OwnerIdsJson),
    canEdit = true,
    isCompletedForCurrentUser = false,
    c.CreatedAt,
    c.UpdatedAt
};
static object ToGroupDto(Group g) => new { g.Id, g.Name, code = g.Code ?? string.Empty, g.IsActive, g.CreatedAt };

public sealed record CourseGroupsRequest(Guid[]? GroupIds);
public sealed record CourseOwnersRequest(Guid[]? OwnerIds);
public sealed record CourseRequest(string? Title, string? Description, bool? IsPublic, Guid[]? VisibleGroupIds, Guid[]? OwnerIds);
public sealed record GroupRequest(string? Name, string? Code, bool? IsActive);
public sealed record GroupMemberRequest(Guid UserId);
