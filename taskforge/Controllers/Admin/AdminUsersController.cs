using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Data;

namespace taskforge.Controllers.Admin
{
    [ApiController]
    [Route("api/admin/users")]
    [Authorize(Roles = AppRoles.Admin)]
    public sealed class AdminUsersController : ControllerBase
    {
        private readonly ApplicationDbContext _db;

        public AdminUsersController(ApplicationDbContext db)
        {
            _db = db;
        }

        public sealed record AdminUserListItemDto(
            Guid Id,
            string Email,
            string FirstName,
            string LastName,
            string FullName,
            string Role,
            bool EmailConfirmed,
            bool LockoutEnabled,
            string? PhoneNumber,
            string? MinecraftNick,
            string? TelegramUsername,
            DateTime CreatedAt,
            DateTime? LastLoginAt,
            DateTime? MinecraftLinkedAtUtc,
            DateTime? TelegramLinkedAtUtc,
            IReadOnlyList<string> FeatureRoles,
            int CodeSolutions,
            int PassedTests,
            int ImageSolutions);

        public sealed record AdminUserStatsDto(int Total, int Linked, int Admins);

        public sealed record AdminUserUpdateDto(
            string Email,
            string FirstName,
            string LastName,
            string Role,
            bool EmailConfirmed,
            bool LockoutEnabled,
            string? PhoneNumber,
            string? MinecraftNick,
            string? TelegramUsername);

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] string? query, [FromQuery] string? role, [FromQuery] bool linkedOnly = false, [FromQuery] int take = 120, CancellationToken ct = default)
        {
            var q = (query ?? string.Empty).Trim().ToLower();
            var rq = (role ?? string.Empty).Trim();

            var usersQuery = _db.Users.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
            {
                usersQuery = usersQuery.Where(u =>
                    u.Email.ToLower().Contains(q) ||
                    u.FirstName.ToLower().Contains(q) ||
                    u.LastName.ToLower().Contains(q) ||
                    ((u.FirstName + " " + u.LastName).ToLower().Contains(q)) ||
                    ((u.MinecraftNick ?? string.Empty).ToLower().Contains(q)) ||
                    ((u.TelegramUsername ?? string.Empty).ToLower().Contains(q)));
            }

            if (!string.IsNullOrWhiteSpace(rq))
                usersQuery = usersQuery.Where(u => u.Role == rq);

            if (linkedOnly)
                usersQuery = usersQuery.Where(u => u.MinecraftLinkedAtUtc != null || u.TelegramLinkedAtUtc != null);

            var users = await usersQuery
                .OrderByDescending(u => u.CreatedAt)
                .Take(Math.Clamp(take, 1, 300))
                .Select(u => new
                {
                    u.Id,
                    u.Email,
                    u.FirstName,
                    u.LastName,
                    u.Role,
                    u.EmailConfirmed,
                    u.LockoutEnabled,
                    u.PhoneNumber,
                    u.MinecraftNick,
                    u.TelegramUsername,
                    u.CreatedAt,
                    u.LastLoginAt,
                    u.MinecraftLinkedAtUtc,
                    u.TelegramLinkedAtUtc
                })
                .ToListAsync(ct);

            var ids = users.Select(x => x.Id).ToList();

            var featureRoles = await _db.UserFeatureRoles
                .AsNoTracking()
                .Where(x => ids.Contains(x.UserId) && x.Role.IsActive)
                .Select(x => new { x.UserId, x.Role.Code })
                .ToListAsync(ct);

            var codeCounts = await _db.UserTaskSolutions.AsNoTracking()
                .Where(x => ids.Contains(x.UserId) && x.PassedAllTests)
                .GroupBy(x => x.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Select(x => x.TaskAssignmentId).Distinct().Count() })
                .ToListAsync(ct);

            var testCounts = await _db.UserTaskTestAttempts.AsNoTracking()
                .Where(x => ids.Contains(x.UserId) && x.Passed)
                .GroupBy(x => x.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Select(x => x.TaskAssignmentId).Distinct().Count() })
                .ToListAsync(ct);

            var imageCounts = await _db.UserImageTaskSolutions.AsNoTracking()
                .Where(x => ids.Contains(x.UserId) && x.Passed == true)
                .GroupBy(x => x.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Select(x => x.TaskAssignmentId).Distinct().Count() })
                .ToListAsync(ct);

            var roleMap = featureRoles.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Code).OrderBy(x => x).ToList());
            var codeMap = codeCounts.ToDictionary(x => x.UserId, x => x.Count);
            var testMap = testCounts.ToDictionary(x => x.UserId, x => x.Count);
            var imageMap = imageCounts.ToDictionary(x => x.UserId, x => x.Count);

            var statsQuery = _db.Users.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
            {
                statsQuery = statsQuery.Where(u =>
                    u.Email.ToLower().Contains(q) ||
                    u.FirstName.ToLower().Contains(q) ||
                    u.LastName.ToLower().Contains(q) ||
                    ((u.FirstName + " " + u.LastName).ToLower().Contains(q)) ||
                    ((u.MinecraftNick ?? string.Empty).ToLower().Contains(q)) ||
                    ((u.TelegramUsername ?? string.Empty).ToLower().Contains(q)));
            }

            if (!string.IsNullOrWhiteSpace(rq))
                statsQuery = statsQuery.Where(u => u.Role == rq);

            if (linkedOnly)
                statsQuery = statsQuery.Where(u => u.MinecraftLinkedAtUtc != null || u.TelegramLinkedAtUtc != null);

            var stats = new AdminUserStatsDto(
                await statsQuery.CountAsync(ct),
                await statsQuery.CountAsync(u => u.MinecraftLinkedAtUtc != null || u.TelegramLinkedAtUtc != null, ct),
                await statsQuery.CountAsync(u => u.Role == AppRoles.Admin, ct));

            var result = users.Select(u => new AdminUserListItemDto(
                u.Id,
                u.Email,
                u.FirstName,
                u.LastName,
                ($"{u.FirstName} {u.LastName}").Trim(),
                u.Role,
                u.EmailConfirmed,
                u.LockoutEnabled,
                u.PhoneNumber,
                u.MinecraftNick,
                u.TelegramUsername,
                u.CreatedAt,
                u.LastLoginAt,
                u.MinecraftLinkedAtUtc,
                u.TelegramLinkedAtUtc,
                roleMap.TryGetValue(u.Id, out var rr) ? rr : Array.Empty<string>(),
                codeMap.TryGetValue(u.Id, out var code) ? code : 0,
                testMap.TryGetValue(u.Id, out var test) ? test : 0,
                imageMap.TryGetValue(u.Id, out var img) ? img : 0
            )).ToList();

            return Ok(new { items = result, stats });
        }



        [HttpDelete("{userId:guid}")]
        public async Task<IActionResult> Delete(Guid userId, CancellationToken ct = default)
        {
            var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null) return NotFound(new { message = "Пользователь не найден." });

            var currentUserIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(currentUserIdClaim, out var currentUserId) && currentUserId == userId)
                return BadRequest(new { message = "Нельзя удалить собственный аккаунт из админки." });

            var ownsCourses = await _db.Courses.AnyAsync(x => x.OwnerId == userId, ct);
            if (ownsCourses)
                return BadRequest(new { message = "Нельзя удалить пользователя, который является владельцем курсов. Сначала передайте владение курсами." });

            var supportTicketIds = await _db.SupportTickets
                .Where(x => x.UserId == userId)
                .Select(x => x.Id)
                .ToListAsync(ct);

            await _db.SupportMessages
                .Where(x => x.AuthorUserId == userId || supportTicketIds.Contains(x.TicketId))
                .ExecuteDeleteAsync(ct);
            await _db.SupportTickets.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);

            await _db.UserTaskSolutions.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.UserImageTaskSolutions.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.UserTaskTestAttempts.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.UserFeatureRoles.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.UserGroupMembers.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.MinecraftWeeklyJoins.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.MinecraftLinkCodes.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.TelegramLinkCodes.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.MinecraftChatMessages.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.CourseOwners.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.UserLoginLogs.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.UserQuotaBuckets.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.UserUiSettings.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.UserBadges.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);

            _db.Users.Remove(user);
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        [HttpPut("{userId:guid}")]
        public async Task<IActionResult> Update(Guid userId, [FromBody] AdminUserUpdateDto dto, CancellationToken ct = default)
        {
            var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null) return NotFound();

            var email = (dto.Email ?? string.Empty).Trim();
            var firstName = (dto.FirstName ?? string.Empty).Trim();
            var lastName = (dto.LastName ?? string.Empty).Trim();
            var role = (dto.Role ?? string.Empty).Trim();
            if (email.Length == 0 || firstName.Length == 0 || lastName.Length == 0 || role.Length == 0)
                return BadRequest(new { message = "Email, имя, фамилия и роль обязательны." });

            if (role != AppRoles.Admin && role != AppRoles.Editor && role != AppRoles.User)
                return BadRequest(new { message = "Недопустимая базовая роль." });

            var dupEmail = await _db.Users.AnyAsync(x => x.Id != userId && x.Email.ToLower() == email.ToLower(), ct);
            if (dupEmail) return Conflict(new { message = "Email уже занят." });

            user.Email = email;
            user.FirstName = firstName;
            user.LastName = lastName;
            user.Role = role;
            user.EmailConfirmed = dto.EmailConfirmed;
            user.LockoutEnabled = dto.LockoutEnabled;
            user.PhoneNumber = string.IsNullOrWhiteSpace(dto.PhoneNumber) ? null : dto.PhoneNumber.Trim();
            user.MinecraftNick = string.IsNullOrWhiteSpace(dto.MinecraftNick) ? null : dto.MinecraftNick.Trim();
            user.TelegramUsername = string.IsNullOrWhiteSpace(dto.TelegramUsername) ? null : dto.TelegramUsername.Trim().TrimStart('@');
            user.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);
            return NoContent();
        }
    }
}
