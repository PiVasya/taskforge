using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public sealed class FeatureRoleService : IFeatureRoleService
    {
        private readonly ApplicationDbContext _db;
        private readonly ILogger<FeatureRoleService> _log;

        public FeatureRoleService(ApplicationDbContext db, ILogger<FeatureRoleService> log)
        {
            _db = db;
            _log = log;
        }

        public async Task EnsureDefaultRolesAsync(CancellationToken ct = default)
        {
            await EnsureRoleAsync(FeatureRoles.Minecraft, "Minecraft", "Доступ к Minecraft-разделу сайта", ct);
            await EnsureRoleAsync(FeatureRoles.AiConsole, "AI Console", "Доступ к AI-разделу и очереди нейросети", ct);
            await EnsureRoleAsync(FeatureRoles.AiAuthoring, "AI Authoring", "Генерация и улучшение заданий через AI", ct);
            await EnsureRoleAsync(FeatureRoles.AiReview, "AI Review", "AI-review решений и аналитика заданий", ct);
            await EnsureRoleAsync(FeatureRoles.AiModeration, "AI Moderation", "AI moderation / risk review пользователей, тикетов и Minecraft", ct);
        }

        public async Task<IReadOnlyList<string>> GetRoleCodesForUserAsync(Guid userId, CancellationToken ct = default)
        {
            return await _db.UserFeatureRoles
                .AsNoTracking()
                .Where(x => x.UserId == userId && x.Role.IsActive)
                .Select(x => x.Role.Code)
                .OrderBy(x => x)
                .ToListAsync(ct);
        }

        public async Task AssignRoleAsync(Guid userId, string roleCode, Guid? assignedByUserId = null, CancellationToken ct = default)
        {
            var role = await GetRoleByCodeAsync(roleCode, ct);
            if (role == null)
                throw new InvalidOperationException($"Feature role '{roleCode}' not found.");

            var exists = await _db.UserFeatureRoles.AnyAsync(x => x.UserId == userId && x.RoleId == role.Id, ct);
            if (exists) return;

            _db.UserFeatureRoles.Add(new UserFeatureRole
            {
                UserId = userId,
                RoleId = role.Id,
                AssignedByUserId = assignedByUserId,
                AssignedAtUtc = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(ct);
        }

        public async Task RemoveRoleAsync(Guid userId, string roleCode, CancellationToken ct = default)
        {
            var role = await GetRoleByCodeAsync(roleCode, ct);
            if (role == null) return;

            var link = await _db.UserFeatureRoles.FirstOrDefaultAsync(x => x.UserId == userId && x.RoleId == role.Id, ct);
            if (link == null) return;

            _db.UserFeatureRoles.Remove(link);
            await _db.SaveChangesAsync(ct);
        }

        public async Task SyncMinecraftLinkedUsersAsync(CancellationToken ct = default)
        {
            await EnsureDefaultRolesAsync(ct);
            var role = await GetRoleByCodeAsync(FeatureRoles.Minecraft, ct);
            if (role == null) return;

            var linkedUserIds = await _db.Users
                .AsNoTracking()
                .Where(x => x.MinecraftNick != null && x.MinecraftNick != "")
                .Select(x => x.Id)
                .ToListAsync(ct);

            if (linkedUserIds.Count == 0) return;

            var existingUserIds = await _db.UserFeatureRoles
                .AsNoTracking()
                .Where(x => x.RoleId == role.Id && linkedUserIds.Contains(x.UserId))
                .Select(x => x.UserId)
                .ToListAsync(ct);

            var missing = linkedUserIds.Except(existingUserIds).ToList();
            if (missing.Count == 0) return;

            foreach (var userId in missing)
            {
                _db.UserFeatureRoles.Add(new UserFeatureRole
                {
                    UserId = userId,
                    RoleId = role.Id,
                    AssignedAtUtc = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Feature role sync completed for Minecraft: assigned {Count} users", missing.Count);
        }

        private async Task<FeatureRole?> GetRoleByCodeAsync(string roleCode, CancellationToken ct)
        {
            var code = (roleCode ?? string.Empty).Trim();
            if (code.Length == 0) return null;

            return await _db.FeatureRoles.FirstOrDefaultAsync(x => x.Code.ToLower() == code.ToLower(), ct);
        }

        private async Task EnsureRoleAsync(string code, string name, string description, CancellationToken ct)
        {
            var existing = await _db.FeatureRoles.FirstOrDefaultAsync(x => x.Code == code, ct);
            if (existing != null)
            {
                var changed = false;
                if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
                {
                    existing.Name = name;
                    changed = true;
                }
                if (!string.Equals(existing.Description, description, StringComparison.Ordinal))
                {
                    existing.Description = description;
                    changed = true;
                }
                if (!existing.IsActive)
                {
                    existing.IsActive = true;
                    changed = true;
                }
                if (changed)
                {
                    existing.UpdatedAtUtc = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
                return;
            }

            _db.FeatureRoles.Add(new FeatureRole
            {
                Id = Guid.NewGuid(),
                Code = code,
                Name = name,
                Description = description,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
    }
}
