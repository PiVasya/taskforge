using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using taskforge.Data;
using taskforge.Data.Models.DTO;

namespace taskforge.Controllers
{
    /// <summary>
    /// Public leaderboard controller.  Provides a leaderboard of students based on the number of
    /// solved assignments.  Normal users only see non‑personal data (names and solved counts).
    /// Administrators/teachers/editors additionally see email and profile picture URL.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class LeaderboardController : ControllerBase
    {
        private readonly ApplicationDbContext _db;

        public LeaderboardController(ApplicationDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Get a leaderboard of users by number of solved assignments.  Optionally filter by course
        /// and time period.  The result is ordered by solved count descending.
        /// </summary>
        /// <param name="courseId">Optional course identifier to limit solutions to a specific course.</param>
        /// <param name="days">Optional number of days to look back.  If omitted, includes all time.</param>
        /// <param name="top">Number of entries to return. Defaults to 20.</param>
        /// <returns>A list of leaderboard entries.</returns>
        [HttpGet]
        [AllowAnonymous]
        public async Task<ActionResult<List<LeaderboardEntryDto>>> GetLeaderboard([FromQuery] Guid? courseId = null, [FromQuery] int? days = null, [FromQuery] int top = 20)
        {
            if (top <= 0) top = 20;

            // Determine whether personal info (email, profile picture) should be revealed.
            var user = HttpContext.User;
            bool includePersonal = false;
            if (user != null && user.Identity?.IsAuthenticated == true)
            {
                // Admin/Teacher/Editor roles can see personal info
                if (user.IsInRole("Admin") || user.IsInRole("Teacher") || user.IsInRole("Editor"))
                {
                    includePersonal = true;
                }
                else
                {
                    // fallback: inspect claims for any of these roles
                    var roles = user.Claims
                        .Where(c => c.Type == ClaimTypes.Role || c.Type == "role" || c.Type == "roles")
                        .Select(c => c.Value)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    includePersonal = roles.Contains("Admin") || roles.Contains("Teacher") || roles.Contains("Editor");
                }
            }

            // Query UserTaskSolutions along with TaskAssignment and Course to filter by course
            var solutionsQuery = _db.UserTaskSolutions
                .Include(s => s.TaskAssignment)
                .Include(s => s.User)
                .AsNoTracking()
                .Where(s => s.PassedAllTests);

            // Filter by course if provided
            if (courseId.HasValue)
            {
                var cid = courseId.Value;
                solutionsQuery = solutionsQuery.Where(s => s.TaskAssignment.CourseId == cid);
            }

            // Filter by days if provided (e.g. 7 days, 30 days, etc.) using UTC times
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTime.UtcNow.AddDays(-days.Value);
                solutionsQuery = solutionsQuery.Where(s => s.SubmittedAt >= since);
            }

            // Group by user and count distinct assignments solved.
            // Without this, multiple submissions for the same task would be counted multiple times.
            var leaderboard = await solutionsQuery
                .GroupBy(s => s.UserId)
                .Select(g => new
                {
                    UserId = g.Key,
                    // Count distinct TaskAssignmentId values to avoid counting duplicate solutions
                    Solved = g.Select(x => x.TaskAssignmentId).Distinct().Count(),
                    FirstName = g.First().User.FirstName,
                    LastName = g.First().User.LastName,
                    Email = g.First().User.Email,
                    ProfilePictureUrl = g.First().User.ProfilePictureUrl
                })
                .OrderByDescending(x => x.Solved)
                .ThenBy(x => x.LastName)
                .ThenBy(x => x.FirstName)
                .Take(top)
                .ToListAsync();

            // Map to DTO with privacy handling
            var result = leaderboard.Select(x => new LeaderboardEntryDto
            {
                UserId = x.UserId,
                FirstName = x.FirstName ?? string.Empty,
                LastName = x.LastName ?? string.Empty,
                Solved = x.Solved,
                Email = includePersonal ? x.Email : null,
                ProfilePictureUrl = includePersonal ? x.ProfilePictureUrl : null
            }).ToList();

            return result;
        }
    }
}
