using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Data;

namespace taskforge.Controllers.Admin
{
    [ApiController]
    [Route("api/admin/minecraft-links")]
    [Authorize(Roles = AppRoles.Admin)]
    public sealed class AdminMinecraftLinksController : ControllerBase
    {
        private readonly ApplicationDbContext _db;

        public AdminMinecraftLinksController(ApplicationDbContext db)
        {
            _db = db;
        }

        public sealed record MinecraftLinkUserDto(
            Guid UserId,
            string Email,
            string FullName,
            string? MinecraftNick,
            string? MinecraftUuid,
            DateTime? LinkedAtUtc,
            int LinkCount,
            int WeeklyJoinEvents,
            int TotalPenalty,
            int TotalScore,
            int EffectiveScore,
            DateTime? LastPenaltyAtUtc,
            IReadOnlyList<string> FeatureRoles);

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] string? query, CancellationToken ct = default)
        {
            var q = (query ?? string.Empty).Trim().ToLower();
            var usersQuery = _db.Users.AsNoTracking().Where(u => u.MinecraftLinkedAtUtc != null || u.MinecraftNick != null || u.MinecraftUuid != null);

            if (!string.IsNullOrWhiteSpace(q))
            {
                usersQuery = usersQuery.Where(u =>
                    u.Email.ToLower().Contains(q) ||
                    ((u.FirstName + " " + u.LastName).ToLower().Contains(q)) ||
                    ((u.MinecraftNick ?? string.Empty).ToLower().Contains(q)) ||
                    ((u.MinecraftUuid ?? string.Empty).ToLower().Contains(q)));
            }

            var users = await usersQuery
                .OrderByDescending(u => u.MinecraftLinkedAtUtc)
                .Take(150)
                .Select(u => new
                {
                    u.Id,
                    u.Email,
                    u.FirstName,
                    u.LastName,
                    u.MinecraftNick,
                    u.MinecraftUuid,
                    u.MinecraftLinkedAtUtc,
                    u.MinecraftLinkCount
                })
                .ToListAsync(ct);

            var ids = users.Select(x => x.Id).ToList();
            if (ids.Count == 0) return Ok(Array.Empty<MinecraftLinkUserDto>());

            var featureRoles = await _db.UserFeatureRoles.AsNoTracking()
                .Where(x => ids.Contains(x.UserId) && x.Role.IsActive)
                .Select(x => new { x.UserId, x.Role.Code })
                .ToListAsync(ct);

            var joins = await _db.MinecraftWeeklyJoins.AsNoTracking()
                .Where(x => ids.Contains(x.UserId))
                .GroupBy(x => x.UserId)
                .Select(g => new
                {
                    UserId = g.Key,
                    WeeklyJoinEvents = g.Count(),
                    TotalPenalty = g.Sum(x => x.PenaltyApplied),
                    LastPenaltyAtUtc = g.Max(x => x.CreatedAtUtc)
                })
                .ToListAsync(ct);

            var scoreMap = await BuildScoreMapAsync(ids, ct);
            var roleMap = featureRoles.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Code).OrderBy(x => x).ToList());
            var joinMap = joins.ToDictionary(x => x.UserId, x => x);

            var result = users.Select(u =>
            {
                var score = scoreMap.TryGetValue(u.Id, out var s) ? s : 0;
                var penalty = joinMap.TryGetValue(u.Id, out var j) ? j.TotalPenalty : 0;
                return new MinecraftLinkUserDto(
                    u.Id,
                    u.Email,
                    ($"{u.FirstName} {u.LastName}").Trim(),
                    u.MinecraftNick,
                    u.MinecraftUuid,
                    u.MinecraftLinkedAtUtc,
                    u.MinecraftLinkCount,
                    joinMap.TryGetValue(u.Id, out var jj) ? jj.WeeklyJoinEvents : 0,
                    penalty,
                    score,
                    score - penalty,
                    joinMap.TryGetValue(u.Id, out var jjj) ? jjj.LastPenaltyAtUtc : null,
                    roleMap.TryGetValue(u.Id, out var rr) ? rr : Array.Empty<string>()
                );
            }).ToList();

            return Ok(result);
        }

        private async Task<Dictionary<Guid, int>> BuildScoreMapAsync(List<Guid> userIds, CancellationToken ct)
        {
            var dict = new Dictionary<Guid, Dictionary<Guid, int>>();

            void Add(Guid userId, Guid assignmentId, int rating)
            {
                if (!dict.TryGetValue(userId, out var inner))
                {
                    inner = new Dictionary<Guid, int>();
                    dict[userId] = inner;
                }
                inner[assignmentId] = inner.TryGetValue(assignmentId, out var cur) ? Math.Max(cur, rating) : rating;
            }

            var codeRows = await _db.UserTaskSolutions.AsNoTracking()
                .Where(x => userIds.Contains(x.UserId) && x.PassedAllTests)
                .Select(x => new { x.UserId, x.TaskAssignmentId, x.TaskAssignment.Rating })
                .ToListAsync(ct);
            foreach (var x in codeRows) Add(x.UserId, x.TaskAssignmentId, x.Rating);

            var testRows = await _db.UserTaskTestAttempts.AsNoTracking()
                .Where(x => userIds.Contains(x.UserId) && x.Passed)
                .Select(x => new { x.UserId, x.TaskAssignmentId, x.TaskAssignment!.Rating })
                .ToListAsync(ct);
            foreach (var x in testRows) Add(x.UserId, x.TaskAssignmentId, x.Rating);

            var imageRows = await _db.UserImageTaskSolutions.AsNoTracking()
                .Where(x => userIds.Contains(x.UserId) && x.Passed == true)
                .Select(x => new { x.UserId, x.TaskAssignmentId, x.TaskAssignment.Rating })
                .ToListAsync(ct);
            foreach (var x in imageRows) Add(x.UserId, x.TaskAssignmentId, x.Rating);

            var mathRows = await _db.UserTaskMathAttempts.AsNoTracking()
                .Where(x => userIds.Contains(x.UserId) && x.Passed)
                .Select(x => new { x.UserId, x.TaskAssignmentId, x.TaskAssignment!.Rating })
                .ToListAsync(ct);
            foreach (var x in mathRows) Add(x.UserId, x.TaskAssignmentId, x.Rating);

            return dict.ToDictionary(x => x.Key, x => x.Value.Values.Sum());
        }
    }
}
