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
            // Сначала вытягиваем все решения (с трекингом выключенным),
            // дальше работаем в памяти — так мы полностью избегаем
            // проблемного GroupBy/let, который EF не умеет транслировать.
            var allSolutions = await _db.UserTaskSolutions
                .AsNoTracking()
                .ToListAsync();

            if (allSolutions.Count == 0)
                return Array.Empty<LeaderboardEntryDto>();

            // Группировка по пользователю и расчёт агрегатов
            var aggregated = allSolutions
                .GroupBy(s => s.UserId)
                .Select(g => new
                {
                    UserId = g.Key,
                    SolvedAssignments = g
                        .Where(x => x.PassedAllTests)
                        .Select(x => x.TaskAssignmentId)
                        .Distinct()
                        .Count(),
                    TotalAttempts = g.Count(),
                    LastSubmitAt = (DateTime?)g.Max(x => x.SubmittedAt)
                })
                .Where(x => x.SolvedAssignments > 0)
                .OrderByDescending(x => x.SolvedAssignments)
                .ThenBy(x => x.LastSubmitAt ?? DateTime.MaxValue)
                .ToList();

            if (aggregated.Count == 0)
                return Array.Empty<LeaderboardEntryDto>();

            // Подгружаем сами User'ов для найденных id
            var userIds = aggregated
                .Select(x => x.UserId)
                .Distinct()
                .ToArray();

            var users = await _db.Users
                .Where(u => userIds.Contains(u.Id))
                .ToListAsync();

            var userMap = users.ToDictionary(u => u.Id);

            var result = new List<LeaderboardEntryDto>();

            // rank считаем по позиции в отсортированном aggregated
            for (var i = 0; i < aggregated.Count; i++)
            {
                var row = aggregated[i];

                if (!userMap.TryGetValue(row.UserId, out var user))
                    continue;

                var extra = ParseExtra(user.AdditionalDataJson);
                if (!extra.ShowInLeaderboard)
                    continue;

                var rank = i + 1;
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
