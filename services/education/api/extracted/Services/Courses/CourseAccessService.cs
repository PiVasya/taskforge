using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Constants;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Courses
{
    public sealed class CourseAccessService : ICourseAccessService
    {
        private readonly ApplicationDbContext _db;

        public CourseAccessService(ApplicationDbContext db)
        {
            _db = db;
        }

        private static bool IsAdmin(string? role)
            => string.Equals(role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase);

        private static bool IsEditor(string? role)
            => string.Equals(role, AppRoles.Editor, StringComparison.OrdinalIgnoreCase);

        private async Task<bool> IsOwnerAsync(Guid userId, Guid courseId)
            => await _db.Courses.AsNoTracking().AnyAsync(c => c.Id == courseId && c.OwnerId == userId)
               || await _db.CourseOwners.AsNoTracking().AnyAsync(x => x.CourseId == courseId && x.UserId == userId);

        public async Task<bool> CanViewCourseAsync(Guid userId, string? role, Guid courseId)
        {
            // Admin can view everything.
            if (IsAdmin(role))
                return true;

            // Владелец курса всегда может его просматривать.
            // Роль Editor НЕ даёт автоматического доступа к чужим курсам.
            if (await IsOwnerAsync(userId, courseId))
                return true;

            var course = await _db.Courses
                .AsNoTracking()
                .Where(c => c.Id == courseId)
                .Select(c => new
                {
                    c.IsPublic,
                    HasGroups = c.VisibleGroups.Any(),
                    GroupIds = c.VisibleGroups.Select(v => v.GroupId)
                })
                .FirstOrDefaultAsync();

            if (course == null)
                return false;

            if (course.IsPublic)
                return true;

            if (!course.HasGroups)
                return false; // private без групп — только Admin/Owner

            var userGroupIds = await _db.UserGroupMembers
                .AsNoTracking()
                .Where(m => m.UserId == userId)
                .Select(m => m.GroupId)
                .ToListAsync();

            return course.GroupIds.Any(gid => userGroupIds.Contains(gid));
        }

        public async Task<bool> CanEditCourseAsync(Guid userId, string? role, Guid courseId)
        {
            // Максимально жёстко: редактировать может только владелец курса.
            // Даже Admin НЕ может редактировать чужие курсы.
            // Роль Editor также не даёт права редактировать чужие курсы.
            _ = role; // role намеренно не используется в проверке прав редактирования
            return await IsOwnerAsync(userId, courseId);
        }

        public async Task<IReadOnlyList<Guid>> GetAccessibleCourseIdsAsync(Guid userId, string? role)
        {
            if (IsAdmin(role))
                return await _db.Courses.AsNoTracking().Select(c => c.Id).ToListAsync();

            // Editor: same visibility as a normal user. Свои курсы показываем всегда (по ownership).

            var userGroupIds = _db.UserGroupMembers
                .AsNoTracking()
                .Where(m => m.UserId == userId)
                .Select(m => m.GroupId);

            return await _db.Courses
                .AsNoTracking()
                .Where(c =>
                    // public
                    c.IsPublic
                    // group-visible
                    || (c.VisibleGroups.Any() && c.VisibleGroups.Any(v => userGroupIds.Contains(v.GroupId)))
                    // own (private тоже должны быть видны владельцу). Поддерживаем оба варианта:
                    // старое поле Course.OwnerId и новую таблицу CourseOwners.
                    || c.OwnerId == userId
                    || c.Owners.Any(o => o.UserId == userId)
                )
                .Select(c => c.Id)
                .ToListAsync();
        }
    }
}
