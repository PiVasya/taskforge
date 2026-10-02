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
    private static async Task<Group?> FindManageableGroupAsync(Guid id, EducationAccessContext access, EducationDbContext db, CancellationToken ct)
    {
        var group = await db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (group is null) return null;
        if (access.IsSuperAdmin) return group;
        if (!access.UserId.HasValue || access.RoleRank < 800) return null;

        var isOwner = await db.GroupOwners.AsNoTracking()
            .AnyAsync(x => x.GroupId == id && x.UserId == access.UserId.Value, ct);
        return CanManageGroup(access, isOwner) ? group : null;
    }

    private static async Task<Dictionary<Guid, Guid[]>> LoadGroupOwnerIdsAsync(IEnumerable<Guid> groupIds, EducationDbContext db, CancellationToken ct)
    {
        var ids = groupIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, Guid[]>();

        var rows = await db.GroupOwners.AsNoTracking()
            .Where(x => ids.Contains(x.GroupId))
            .Select(x => new { x.GroupId, x.UserId })
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.GroupId)
            .ToDictionary(x => x.Key, x => x.Select(row => row.UserId).Distinct().ToArray());
    }

    private static async Task<bool> AreValidGroupOwnersAsync(Guid[] ownerUserIds, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        if (ownerUserIds.Length == 0) return false;
        var levels = await LoadUserAccessLevelsAsync(ownerUserIds, clients, cfg, ct);
        var adminIds = levels
            .Where(x => x.EffectiveRank >= 800)
            .Select(x => x.UserId)
            .ToHashSet();
        return ownerUserIds.All(adminIds.Contains);
    }

    private static async Task ReplaceGroupOwnersAsync(Guid groupId, Guid[] ownerUserIds, EducationDbContext db, CancellationToken ct)
    {
        var requested = ownerUserIds.Where(x => x != Guid.Empty).Distinct().ToHashSet();
        var existing = await db.GroupOwners.Where(x => x.GroupId == groupId).ToListAsync(ct);
        var existingIds = existing.Select(x => x.UserId).ToHashSet();

        db.GroupOwners.RemoveRange(existing.Where(x => !requested.Contains(x.UserId)));
        db.GroupOwners.AddRange(requested
            .Where(x => !existingIds.Contains(x))
            .Select(userId => new GroupOwner { GroupId = groupId, UserId = userId }));
    }

    private static IResult GroupAdminForbidden() => Microsoft.AspNetCore.Http.Results.Json(
        new { message = "Управление группами доступно только администраторам.", code = "GROUP_ADMIN_FORBIDDEN" },
        statusCode: StatusCodes.Status403Forbidden);

    private static IResult GroupOwnersRequired() => Microsoft.AspNetCore.Http.Results.BadRequest(
        new { message = "У группы должен быть хотя бы один владелец.", code = "GROUP_OWNERS_REQUIRED" });

    private static IResult GroupOwnerInvalid() => Microsoft.AspNetCore.Http.Results.BadRequest(
        new { message = "Владельцами группы могут быть только Admin или SuperAdmin.", code = "GROUP_OWNER_INVALID" });

    private static IResult GroupOwnerManagementForbidden() => Microsoft.AspNetCore.Http.Results.Json(
        new { message = "Список владельцев группы может менять только SuperAdmin.", code = "GROUP_OWNER_MANAGEMENT_FORBIDDEN" },
        statusCode: StatusCodes.Status403Forbidden);

    private static WebApplication MapGroupsEndpoints(WebApplication app)
    {
        app.MapGet("/api/groups", async (HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            IQueryable<Group> query = db.Groups.AsNoTracking();
            if (access.IsSuperAdmin)
            {
                query = query.OrderBy(x => x.Name);
            }
            else if (access.RoleRank >= 800)
            {
                var userId = access.UserId.Value;
                query = query
                    .Where(x => db.GroupOwners.Any(owner => owner.GroupId == x.Id && owner.UserId == userId))
                    .OrderBy(x => x.Name);
            }
            else
            {
                query = query.Where(x => access.GroupIds.Contains(x.Id)).OrderBy(x => x.Name);
            }

            var rows = await query.ToListAsync(ct);
            var owners = access.RoleRank >= 800
                ? await LoadGroupOwnerIdsAsync(rows.Select(x => x.Id), db, ct)
                : new Dictionary<Guid, Guid[]>();
            return Microsoft.AspNetCore.Http.Results.Ok(rows
                .Select(x => ToGroupDto(x, ownerUserIds: owners.GetValueOrDefault(x.Id, Array.Empty<Guid>())))
                .ToList());
        });

        app.MapGet("/api/admin/groups", async (bool? mineOnly, HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            if (access.RoleRank < 800) return GroupAdminForbidden();

            IQueryable<Group> query = db.Groups.AsNoTracking();
            if (!access.IsSuperAdmin || mineOnly == true)
            {
                var userId = access.UserId.Value;
                query = query.Where(x => db.GroupOwners.Any(owner => owner.GroupId == x.Id && owner.UserId == userId));
            }

            var rows = await query.OrderBy(x => x.Name).ToListAsync(ct);
            var ids = rows.Select(x => x.Id).ToArray();
            var counts = ids.Length == 0
                ? new Dictionary<Guid, int>()
                : await db.GroupMembers.AsNoTracking()
                    .Where(x => ids.Contains(x.GroupId))
                    .GroupBy(x => x.GroupId)
                    .ToDictionaryAsync(x => x.Key, x => x.Count(), ct);
            var owners = await LoadGroupOwnerIdsAsync(ids, db, ct);

            return Microsoft.AspNetCore.Http.Results.Ok(rows
                .Select(x => ToGroupDto(x, counts.GetValueOrDefault(x.Id), owners.GetValueOrDefault(x.Id, Array.Empty<Guid>())))
                .ToList());
        });

        app.MapPost("/api/admin/groups", async (GroupRequest request, HttpContext http, EducationDbContext db, IConfiguration cfg, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            if (access.RoleRank < 800) return GroupAdminForbidden();
            if (string.IsNullOrWhiteSpace(request.Name))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Введите название группы.", code = "GROUP_NAME_REQUIRED" });

            Guid[] ownerUserIds;
            if (request.OwnerUserIds is null)
            {
                ownerUserIds = new[] { access.UserId.Value };
            }
            else
            {
                ownerUserIds = request.OwnerUserIds.Where(x => x != Guid.Empty).Distinct().ToArray();
                if (ownerUserIds.Length == 0) return GroupOwnersRequired();
                if (!access.IsSuperAdmin && (ownerUserIds.Length != 1 || ownerUserIds[0] != access.UserId.Value))
                    return GroupOwnerManagementForbidden();
            }
            if (!await AreValidGroupOwnersAsync(ownerUserIds, clients, cfg, ct)) return GroupOwnerInvalid();

            var group = new Group
            {
                Name = request.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()
            };
            db.Groups.Add(group);
            db.GroupOwners.AddRange(ownerUserIds.Select(userId => new GroupOwner { GroupId = group.Id, UserId = userId }));
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToGroupDto(group, ownerUserIds: ownerUserIds));
        });

        app.MapPut("/api/admin/groups/{id:guid}", async (Guid id, GroupRequest request, HttpContext http, EducationDbContext db, IConfiguration cfg, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            if (access.RoleRank < 800) return GroupAdminForbidden();
            var group = await FindManageableGroupAsync(id, access, db, ct);
            if (group == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Введите название группы.", code = "GROUP_NAME_REQUIRED" });

            Guid[]? requestedOwners = null;
            if (request.OwnerUserIds is not null)
            {
                if (!CanChangeGroupOwners(access)) return GroupOwnerManagementForbidden();
                requestedOwners = request.OwnerUserIds.Where(x => x != Guid.Empty).Distinct().ToArray();
                if (requestedOwners.Length == 0) return GroupOwnersRequired();
                if (!await AreValidGroupOwnersAsync(requestedOwners, clients, cfg, ct)) return GroupOwnerInvalid();
            }

            group.Name = request.Name.Trim();
            group.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            if (requestedOwners is not null) await ReplaceGroupOwnersAsync(group.Id, requestedOwners, db, ct);
            await db.SaveChangesAsync(ct);

            var membersCount = await db.GroupMembers.CountAsync(x => x.GroupId == group.Id, ct);
            var ownerUserIds = requestedOwners ?? await db.GroupOwners.AsNoTracking()
                .Where(x => x.GroupId == group.Id)
                .Select(x => x.UserId)
                .ToArrayAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToGroupDto(group, membersCount, ownerUserIds));
        });

        app.MapDelete("/api/admin/groups/{id:guid}", async (Guid id, HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            if (access.RoleRank < 800) return GroupAdminForbidden();
            var group = await FindManageableGroupAsync(id, access, db, ct);
            if (group == null) return Microsoft.AspNetCore.Http.Results.NotFound();

            var courses = await db.Courses.Where(x => x.VisibleGroupIdsJson.Contains(id.ToString())).ToListAsync(ct);
            foreach (var course in courses)
            {
                var ids = DeserializeIds(course.VisibleGroupIdsJson).Where(x => x != id).ToArray();
                course.VisibleGroupIdsJson = Serialize(ids);
                course.UpdatedAt = DateTimeOffset.UtcNow;
            }

            db.Groups.Remove(group);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { message = "deleted" });
        });

        app.MapGet("/api/admin/groups/{groupId:guid}/members", async (Guid groupId, HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            if (access.RoleRank < 800) return GroupAdminForbidden();
            var group = await FindManageableGroupAsync(groupId, access, db, ct);
            if (group == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Группа не найдена.", code = "GROUP_NOT_FOUND" });

            var userIds = await db.GroupMembers.AsNoTracking()
                .Where(x => x.GroupId == groupId)
                .Select(x => x.UserId)
                .ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { groupId, userIds });
        });

        app.MapPost("/api/admin/groups/{groupId:guid}/members", async (Guid groupId, GroupMemberRequest request, HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            if (access.RoleRank < 800) return GroupAdminForbidden();
            var group = await FindManageableGroupAsync(groupId, access, db, ct);
            if (group == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Группа не найдена.", code = "GROUP_NOT_FOUND" });

            if (!await db.GroupMembers.AnyAsync(x => x.GroupId == groupId && x.UserId == request.UserId, ct))
            {
                db.GroupMembers.Add(new GroupMember { GroupId = groupId, UserId = request.UserId });
                await db.SaveChangesAsync(ct);
            }
            return Microsoft.AspNetCore.Http.Results.Ok(new { groupId, request.UserId });
        });

        app.MapDelete("/api/admin/groups/{groupId:guid}/members/{userId:guid}", async (Guid groupId, Guid userId, HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            if (access.RoleRank < 800) return GroupAdminForbidden();
            var group = await FindManageableGroupAsync(groupId, access, db, ct);
            if (group == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Группа не найдена.", code = "GROUP_NOT_FOUND" });

            var rows = await db.GroupMembers.Where(x => x.GroupId == groupId && x.UserId == userId).ToListAsync(ct);
            db.GroupMembers.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { groupId, userId });
        });

        return app;
    }
}
