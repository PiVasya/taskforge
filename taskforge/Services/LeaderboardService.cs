// taskforge/Services/LeaderboardService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.Entities;
using taskforge.Data.Models.Profile;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    /// <summary>
    /// Сервис общего рейтинга и публичных профилей.
    /// Использует UserTaskSolutions и AdditionalDataJson.
    /// </summary>
    public sealed class LeaderboardService : ILeaderboardService
    {
        private readonly ApplicationDbContext _db;

        public LeaderboardService(ApplicationDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Публичный топ пользователей.
        /// Считает:
        /// - количество уникальных решённых заданий (PassedAllTests = true)
        /// - количество попыток
        /// - последнюю отправку
        /// Режет тех, у кого в профиле ShowInLeaderboard = false.
        /// </summary>
        public async Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync()
        {
            var query =
                from s in _db.UserTaskSolutions.AsNoTracking()
                group s by s.UserId
                into g
                let solvedAssignments = g
                    .Where(x => x.PassedAllTests)
                    .Select(x => x.TaskAssignmentId)
                    .Distinct()
                    .Count()
                let totalAttempts = g.Count()
                let lastSubmitAt = g.Max(x => (DateTime?)x.SubmittedAt)
                join u in _db.Users on g.Key equals u.Id
                select new
                {
                    User = u,
                    SolvedAssignments = solvedAssignments,
                    TotalAttempts = totalAttempts,
                    LastSubmitAt = lastSubmitAt,
                    u.AdditionalDataJson
                };

            var rows = await query
                .Where(x => x.SolvedAssignments > 0)
                .ToListAsync();

            // сортируем: сначала по решённым, потом по давности
            var ordered = rows
                .OrderByDescending(x => x.SolvedAssignments)
                .ThenBy(x => x.LastSubmitAt ?? DateTime.MaxValue)
                .ToList();

            var result = new List<LeaderboardEntryDto>(ordered.Count);

            for (var i = 0; i < ordered.Count; i++)
            {
                var row = ordered[i];
                var rank = i + 1;
                var user = row.User;

                var extra = ParseExtra(row.AdditionalDataJson);
                if (!extra.ShowInLeaderboard)
                    continue;

                var displayName = BuildDisplayName(user);

                result.Add(new LeaderboardEntryDto
                {
                    UserId = user.Id,
                    Rank = rank,
                    DisplayName = displayName,
                    Email = user.Email,

                    // для совместимости с админским топом
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Solved = row.SolvedAssignments,

                    // для публичного топа
                    SolvedAssignments = row.SolvedAssignments,
                    TotalAttempts = row.TotalAttempts,
                    LastSubmitAt = row.LastSubmitAt,

                    // ава и доп.данные
                    AvatarUrl = user.ProfilePictureUrl,
                    Location = extra.Location,
                    Education = extra.Education
                });
            }

            return result;
        }

        /// <summary>
        /// Публичный профиль пользователя (открывается из топа).
        /// </summary>
        public async Task<PublicUserProfileDto?> GetPublicProfileAsync(Guid userId)
        {
            var user = await _db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null) return null;

            var solvesQuery = _db.UserTaskSolutions
                .AsNoTracking()
                .Where(s => s.UserId == userId);

            var solvedAssignments = await solvesQuery
                .Where(x => x.PassedAllTests)
                .Select(x => x.TaskAssignmentId)
                .Distinct()
                .CountAsync();

            var totalAttempts = await solvesQuery.CountAsync();

            // считаем rank через общий топ
            var leaderboard = await GetLeaderboardAsync();
            var rank = leaderboard.FirstOrDefault(e => e.UserId == userId)?.Rank ?? 0;

            var extra = ParseExtra(user.AdditionalDataJson);

            return new PublicUserProfileDto
            {
                Id = user.Id,
                DisplayName = BuildDisplayName(user),
                Email = user.Email,
                AvatarUrl = user.ProfilePictureUrl,

                Bio = extra.Bio,
                Location = extra.Location,
                Education = extra.Education,
                Skills = extra.Skills,

                Github = extra.Links.Github,
                Telegram = extra.Links.Telegram,
                Website = extra.Links.Website,

                Rank = rank,
                SolvedAssignments = solvedAssignments,
                TotalAttempts = totalAttempts
            };
        }

        private static UserProfileExtra ParseExtra(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new UserProfileExtra();

            try
            {
                return JsonSerializer.Deserialize<UserProfileExtra>(json) ?? new UserProfileExtra();
            }
            catch
            {
                return new UserProfileExtra();
            }
        }

        private static string BuildDisplayName(User user)
        {
            var parts = new[]
            {
                user.FirstName?.Trim(),
                user.LastName?.Trim()
            }
            .Where(x => !string.IsNullOrEmpty(x))
            .ToArray();

            if (parts.Length == 0)
                return user.Email;

            return string.Join(" ", parts);
        }
    }
}
