using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;

using TaskForge.Education.Api.Contracts;
using static TaskForge.Education.Api.Services.Access.EducationApiAccessService;
using static TaskForge.Education.Api.Services.Common.EducationApiCommonService;
using static TaskForge.Education.Api.Services.Mapping.EducationApiMappingService;
using static TaskForge.Education.Api.Services.Serialization.EducationApiSerializationService;

namespace TaskForge.Education.Api.Endpoints;

internal static partial class EducationApiEndpoints
{
    private static WebApplication MapGroupsEndpoints(WebApplication app)
    {
        app.MapGet("/api/groups", async (HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var query = db.Groups.AsNoTracking().OrderBy(x => x.Name);
            if (!access.IsEditorOrAdmin)
            {
                query = query.Where(x => access.GroupIds.Contains(x.Id)).OrderBy(x => x.Name);
            }

            var rows = await query.ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToGroupDto(x, showCode: access.IsEditorOrAdmin)).ToList());
        });

        app.MapGet("/api/admin/groups", async (EducationDbContext db) => Microsoft.AspNetCore.Http.Results.Ok((await db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync()).Select(x => ToGroupDto(x, showCode: true)).ToList()));

        app.MapPost("/api/admin/groups", async (GroupRequest request, EducationDbContext db) =>
        {
            var group = new Group { Name = string.IsNullOrWhiteSpace(request.Name) ? "Новая группа" : request.Name.Trim(), Code = request.Code, IsActive = request.IsActive ?? true };
            db.Groups.Add(group);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToGroupDto(group, showCode: true));
        });

        app.MapPut("/api/admin/groups/{id:guid}", async (Guid id, GroupRequest request, EducationDbContext db) =>
        {
            var group = await db.Groups.FindAsync(id);
            if (group == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            if (!string.IsNullOrWhiteSpace(request.Name)) group.Name = request.Name.Trim();
            group.Code = request.Code;
            if (request.IsActive.HasValue) group.IsActive = request.IsActive.Value;
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToGroupDto(group, showCode: true));
        });

        app.MapDelete("/api/admin/groups/{id:guid}", async (Guid id, EducationDbContext db) =>
        {
            var group = await db.Groups.FindAsync(id);
            if (group == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            db.Groups.Remove(group);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(new { message = "deleted" });
        });

        app.MapPost("/api/admin/groups/{groupId:guid}/members", async (Guid groupId, GroupMemberRequest request, EducationDbContext db) =>
        {
            if (!await db.GroupMembers.AnyAsync(x => x.GroupId == groupId && x.UserId == request.UserId))
            {
                db.GroupMembers.Add(new GroupMember { GroupId = groupId, UserId = request.UserId });
                await db.SaveChangesAsync();
            }
            return Microsoft.AspNetCore.Http.Results.Ok(new { groupId, request.UserId });
        });

        app.MapDelete("/api/admin/groups/{groupId:guid}/members/{userId:guid}", async (Guid groupId, Guid userId, EducationDbContext db) =>
        {
            var rows = await db.GroupMembers.Where(x => x.GroupId == groupId && x.UserId == userId).ToListAsync();
            db.GroupMembers.RemoveRange(rows);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(new { groupId, userId });
        });

        return app;
    }
}
