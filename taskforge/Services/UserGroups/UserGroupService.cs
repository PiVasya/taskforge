using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO.UserGroups;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Services.UserGroups
{
    public sealed class UserGroupService : IUserGroupService
    {
        private readonly ApplicationDbContext _db;

        public UserGroupService(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IReadOnlyList<UserGroupDto>> GetAllAsync(bool includeInactive)
        {
            var q = _db.UserGroups.AsNoTracking();
            if (!includeInactive)
                q = q.Where(g => g.IsActive);

            return await q
                .OrderBy(g => g.Name)
                .Select(g => new UserGroupDto
                {
                    Id = g.Id,
                    Name = g.Name,
                    Code = g.Code,
                    Description = g.Description,
                    IsActive = g.IsActive,
                    Color = g.Color,
                    Icon = g.Icon,
                    ExternalId = g.ExternalId,
                    Notes = g.Notes,
                    TagsJson = g.TagsJson,
                    CreatedAt = g.CreatedAt,
                    UpdatedAt = g.UpdatedAt,
                    MembersCount = _db.UserGroupMembers.Count(m => m.GroupId == g.Id)
                })
                .ToListAsync();
        }

        public async Task<UserGroupDto?> GetByIdAsync(Guid groupId)
        {
            return await _db.UserGroups.AsNoTracking()
                .Where(g => g.Id == groupId)
                .Select(g => new UserGroupDto
                {
                    Id = g.Id,
                    Name = g.Name,
                    Code = g.Code,
                    Description = g.Description,
                    IsActive = g.IsActive,
                    Color = g.Color,
                    Icon = g.Icon,
                    ExternalId = g.ExternalId,
                    Notes = g.Notes,
                    TagsJson = g.TagsJson,
                    CreatedAt = g.CreatedAt,
                    UpdatedAt = g.UpdatedAt,
                    MembersCount = _db.UserGroupMembers.Count(m => m.GroupId == g.Id)
                })
                .FirstOrDefaultAsync();
        }

        public async Task<IReadOnlyList<Guid>> GetUserGroupIdsAsync(Guid userId)
        {
            return await _db.UserGroupMembers
                .AsNoTracking()
                .Where(m => m.UserId == userId)
                .Select(m => m.GroupId)
                .ToListAsync();
        }

        public async Task<Guid> CreateAsync(CreateUserGroupRequest request)
        {
            var now = DateTime.UtcNow;
            var entity = new UserGroup
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                Code = request.Code.Trim(),
                Description = request.Description?.Trim(),
                IsActive = request.IsActive,
                Color = request.Color?.Trim(),
                Icon = request.Icon?.Trim(),
                ExternalId = request.ExternalId?.Trim(),
                Notes = request.Notes,
                TagsJson = request.TagsJson,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.UserGroups.Add(entity);
            await _db.SaveChangesAsync();
            return entity.Id;
        }

        public async Task UpdateAsync(Guid groupId, UpdateUserGroupRequest request)
        {
            var entity = await _db.UserGroups.FirstOrDefaultAsync(g => g.Id == groupId)
                         ?? throw new KeyNotFoundException("Group not found");

            entity.Name = request.Name.Trim();
            entity.Code = request.Code.Trim();
            entity.Description = request.Description?.Trim();
            entity.IsActive = request.IsActive;
            entity.Color = request.Color?.Trim();
            entity.Icon = request.Icon?.Trim();
            entity.ExternalId = request.ExternalId?.Trim();
            entity.Notes = request.Notes;
            entity.TagsJson = request.TagsJson;
            entity.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Guid groupId)
        {
            var entity = await _db.UserGroups.FirstOrDefaultAsync(g => g.Id == groupId)
                         ?? throw new KeyNotFoundException("Group not found");

            _db.UserGroups.Remove(entity);
            await _db.SaveChangesAsync();
        }

        public async Task AddMemberAsync(Guid groupId, Guid userId)
        {
            var exists = await _db.UserGroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId);
            if (exists) return;

            _db.UserGroupMembers.Add(new UserGroupMember
            {
                GroupId = groupId,
                UserId = userId,
                AddedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        public async Task RemoveMemberAsync(Guid groupId, Guid userId)
        {
            await _db.UserGroupMembers
                .Where(m => m.GroupId == groupId && m.UserId == userId)
                .ExecuteDeleteAsync();
        }
    }
}
