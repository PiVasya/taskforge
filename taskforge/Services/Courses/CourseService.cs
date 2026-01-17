using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Constants;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public sealed class CourseService : ICourseService
    {
        private readonly ApplicationDbContext _db;
        private readonly ICourseAccessService _access;

        public CourseService(ApplicationDbContext db, ICourseAccessService access)
        {
            _db = db;
            _access = access;
        }

        private static bool IsAdminOrEditor(string? role)
            => string.Equals(role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)
               || string.Equals(role, AppRoles.Editor, StringComparison.OrdinalIgnoreCase);

        public async Task<Guid> CreateAsync(CreateCourseRequest req, Guid currentUserId, string? role)
        {
            if (!IsAdminOrEditor(role))
                throw new UnauthorizedAccessException("Only Admin or Editor can create courses.");

            var now = DateTime.UtcNow;
            var course = new Course
            {
                Id = Guid.NewGuid(),
                Title = req.Title.Trim(),
                Description = req.Description,
                IsPublic = req.IsPublic,
                OwnerId = currentUserId,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.Courses.Add(course);

            // owners: создатель + доп.
            var ownerIds = new HashSet<Guid> { currentUserId };
            if (req.OwnerIds != null)
            {
                foreach (var oid in req.OwnerIds)
                    ownerIds.Add(oid);
            }

            foreach (var oid in ownerIds)
            {
                _db.CourseOwners.Add(new CourseOwner
                {
                    CourseId = course.Id,
                    UserId = oid,
                    AddedAt = now,
                    AddedByUserId = currentUserId
                });
            }

            // visible groups
            if (req.VisibleGroupIds != null)
            {
                foreach (var gid in req.VisibleGroupIds.Distinct())
                {
                    _db.CourseVisibleGroups.Add(new CourseVisibleGroup
                    {
                        CourseId = course.Id,
                        GroupId = gid,
                        AddedAt = now,
                        AddedByUserId = currentUserId
                    });
                }
            }

            await _db.SaveChangesAsync();
            return course.Id;
        }

        public async Task<IReadOnlyList<CourseListItemDto>> GetListAsync(Guid currentUserId, string? role)
        {
            var accessibleCourseIds = await _access.GetAccessibleCourseIdsAsync(currentUserId, role);

            var data = await _db.Courses.AsNoTracking()
                .Where(c => accessibleCourseIds.Contains(c.Id))
                .Include(c => c.Assignments)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            var result = new List<CourseListItemDto>(data.Count);
            foreach (var c in data)
            {
                var codeAssignmentIds = c.Assignments.Where(a => a.Type != "test").Select(a => a.Id).ToList();
                var testAssignmentIds = c.Assignments.Where(a => a.Type == "test").Select(a => a.Id).ToList();

                var solvedCodes = await _db.UserTaskSolutions.AsNoTracking()
                    .Where(s => s.UserId == currentUserId && s.PassedAllTests && codeAssignmentIds.Contains(s.TaskAssignmentId))
                    .Select(s => s.TaskAssignmentId)
                    .Distinct()
                    .CountAsync();

                var solvedTests = await _db.UserTaskTestAttempts.AsNoTracking()
                    .Where(t => t.UserId == currentUserId && t.Passed && testAssignmentIds.Contains(t.TaskAssignmentId))
                    .Select(t => t.TaskAssignmentId)
                    .Distinct()
                    .CountAsync();

                var canEdit = await _access.CanEditCourseAsync(currentUserId, role, c.Id);

                var total = codeAssignmentIds.Count + testAssignmentIds.Count;
                var completed = total > 0 && (solvedCodes + solvedTests) == total;

                var ownerIds = await _db.CourseOwners.AsNoTracking()
                    .Where(o => o.CourseId == c.Id)
                    .Select(o => o.UserId)
                    .ToListAsync();

                var visibleGroupIds = await _db.CourseVisibleGroups.AsNoTracking()
                    .Where(v => v.CourseId == c.Id)
                    .Select(v => v.GroupId)
                    .ToListAsync();

                result.Add(new CourseListItemDto
                {
                    Id = c.Id,
                    Title = c.Title,
                    Description = c.Description,
                    IsPublic = c.IsPublic,
                    OwnerId = c.OwnerId,
                    CreatedAt = c.CreatedAt,
                    AssignmentCount = codeAssignmentIds.Count,
                    SolvedCountForCurrentUser = solvedCodes,
                    TestCount = testAssignmentIds.Count,
                    SolvedTestsCountForCurrentUser = solvedTests,
                    IsCompletedForCurrentUser = completed,
                    CanEdit = canEdit,
                    OwnerIds = ownerIds,
                    VisibleGroupIds = visibleGroupIds
                });
            }

            return result;
        }

        public async Task<CourseDetailsDto?> GetDetailsAsync(Guid courseId, Guid currentUserId, string? role)
        {
            var canView = await _access.CanViewCourseAsync(currentUserId, role, courseId);
            if (!canView)
                return null;

            var c = await _db.Courses.AsNoTracking()
                .Include(x => x.Assignments)
                .FirstOrDefaultAsync(x => x.Id == courseId);

            if (c == null) return null;

            var codeAssignmentIds = c.Assignments.Where(a => a.Type != "test").Select(a => a.Id).ToList();
            var testAssignmentIds = c.Assignments.Where(a => a.Type == "test").Select(a => a.Id).ToList();

            var solvedCodes = await _db.UserTaskSolutions.AsNoTracking()
                .Where(s => s.UserId == currentUserId && s.PassedAllTests && codeAssignmentIds.Contains(s.TaskAssignmentId))
                .Select(s => s.TaskAssignmentId)
                .Distinct()
                .CountAsync();

            var solvedTests = await _db.UserTaskTestAttempts.AsNoTracking()
                .Where(t => t.UserId == currentUserId && t.Passed && testAssignmentIds.Contains(t.TaskAssignmentId))
                .Select(t => t.TaskAssignmentId)
                .Distinct()
                .CountAsync();

            var total = codeAssignmentIds.Count + testAssignmentIds.Count;
            var completed = total > 0 && (solvedCodes + solvedTests) == total;

            var canEdit = await _access.CanEditCourseAsync(currentUserId, role, courseId);

            var ownerIds = await _db.CourseOwners.AsNoTracking()
                .Where(o => o.CourseId == courseId)
                .Select(o => o.UserId)
                .ToListAsync();

            var visibleGroupIds = await _db.CourseVisibleGroups.AsNoTracking()
                .Where(v => v.CourseId == courseId)
                .Select(v => v.GroupId)
                .ToListAsync();

            return new CourseDetailsDto
            {
                Id = c.Id,
                Title = c.Title,
                Description = c.Description ?? string.Empty,
                IsPublic = c.IsPublic,
                OwnerId = c.OwnerId,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                AssignmentCount = codeAssignmentIds.Count,
                SolvedCountForCurrentUser = solvedCodes,
                TestCount = testAssignmentIds.Count,
                SolvedTestsCountForCurrentUser = solvedTests,
                IsCompletedForCurrentUser = completed,
                CanEdit = canEdit,
                OwnerIds = ownerIds,
                VisibleGroupIds = visibleGroupIds
            };
        }

        public async Task UpdateAsync(Guid courseId, Guid currentUserId, string? role, UpdateCourseRequest request)
        {
            var canEdit = await _access.CanEditCourseAsync(currentUserId, role, courseId);
            if (!canEdit)
                throw new UnauthorizedAccessException("Only Admin or course owner can edit the course.");

            var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == courseId)
                         ?? throw new KeyNotFoundException("Course not found");

            course.Title = request.Title.Trim();
            course.Description = request.Description?.Trim();
            course.IsPublic = request.IsPublic;
            course.UpdatedAt = DateTime.UtcNow;

            var now = DateTime.UtcNow;

            if (request.VisibleGroupIds != null)
            {
                // полная замена
                await _db.CourseVisibleGroups.Where(v => v.CourseId == courseId).ExecuteDeleteAsync();
                foreach (var gid in request.VisibleGroupIds.Distinct())
                {
                    _db.CourseVisibleGroups.Add(new CourseVisibleGroup
                    {
                        CourseId = courseId,
                        GroupId = gid,
                        AddedAt = now,
                        AddedByUserId = currentUserId
                    });
                }
            }

            if (request.OwnerIds != null)
            {
                // полная замена owners. Всегда включаем создателя (OwnerId) + текущего юзера (на всякий)
                var ownerIds = new HashSet<Guid>(request.OwnerIds);
                ownerIds.Add(course.OwnerId);
                ownerIds.Add(currentUserId);

                await _db.CourseOwners.Where(o => o.CourseId == courseId).ExecuteDeleteAsync();
                foreach (var oid in ownerIds)
                {
                    _db.CourseOwners.Add(new CourseOwner
                    {
                        CourseId = courseId,
                        UserId = oid,
                        AddedAt = now,
                        AddedByUserId = currentUserId
                    });
                }
            }

            await _db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Guid courseId, Guid currentUserId, string? role)
        {
            var canEdit = await _access.CanEditCourseAsync(currentUserId, role, courseId);
            if (!canEdit)
                throw new UnauthorizedAccessException("Only Admin or course owner can delete the course.");

            var course = await _db.Courses
                .Include(c => c.Assignments)
                    .ThenInclude(a => a.TestCases)
                .FirstOrDefaultAsync(c => c.Id == courseId)
                ?? throw new KeyNotFoundException("Course not found");

            _db.Courses.Remove(course);
            await _db.SaveChangesAsync();
        }
    }
}
