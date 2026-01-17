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

        public async Task<bool> CanViewCourseAsync(Guid userId, string? role, Guid courseId)
        {
            if (IsAdmin(role) || IsEditor(role))
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
                return false; // private без групп — только Admin/Editor

            var userGroupIds = await _db.UserGroupMembers
                .AsNoTracking()
                .Where(m => m.UserId == userId)
                .Select(m => m.GroupId)
                .ToListAsync();

            return course.GroupIds.Any(gid => userGroupIds.Contains(gid));
        }

        public async Task<bool> CanEditCourseAsync(Guid userId, string? role, Guid courseId)
        {
            if (IsAdmin(role))
                return true;

            if (!IsEditor(role))
                return false;

            // Editor может редактировать только если owner
            return await _db.CourseOwners
                .AsNoTracking()
                .AnyAsync(x => x.CourseId == courseId && x.UserId == userId);
        }

        public async Task<IReadOnlyList<Guid>> GetAccessibleCourseIdsAsync(Guid userId, string? role)
        {
            if (IsAdmin(role) || IsEditor(role))
            {
                return await _db.Courses.AsNoTracking().Select(c => c.Id).ToListAsync();
            }

            var userGroupIds = _db.UserGroupMembers
                .AsNoTracking()
                .Where(m => m.UserId == userId)
                .Select(m => m.GroupId);

            return await _db.Courses
                .AsNoTracking()
                .Where(c =>
                    c.IsPublic
                    || (c.VisibleGroups.Any() && c.VisibleGroups.Any(v => userGroupIds.Contains(v.GroupId)))
                )
                .Select(c => c.Id)
                .ToListAsync();
        }
    }
}
