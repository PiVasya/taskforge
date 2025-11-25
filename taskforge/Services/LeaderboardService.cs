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
    /// Добавлена поддержка фильтров: курс, дни, группа и ограничение по количеству.
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
        /// Фильтры по курсу, дням и группе позволяют получать выборку за определённый период
        /// или только по конкретному курсу/группе.
        /// </summary>
        public async Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(
            Guid? courseId = null,
            int? days = null,
            Guid? groupId = null,
            int? top = null)
        {
            // базовый запрос по решениям
            var q = _db.UserTaskSolutions
                .AsNoTracking()
                .AsQueryable();

            // для фильтра по курсу или группе нам нужен TaskAssignment
            if (courseId.HasValue || groupId.HasValue)
            {
                q = q.Include(s => s.TaskAssignment);
            }

            // фильтр по курсу
            if (courseId.HasValue)
            {
                q = q.Where(s => s.TaskAssignment.CourseId == courseId.Value);
            }

            // фильтр по количеству дней
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTime.UtcNow.AddDays(-days.Value);
                q = q.Where(s => s.SubmittedAt >= since);
            }

            // TODO: фильтр по группе (когда появятся группы)
            if (groupId.HasValue)
            {
                // пока группы не реализованы, фильтр игнорируется
            }

            // загружаем все подходящие решения
            var allSolutions = await q.ToListAsync();
            if (allSolutions.Count == 0)
                return Array.Empty<LeaderboardEntryDto>();

            // агрегация по пользователю
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

            // ограничение по top
            if (top.HasValue && top.Value > 0 && aggregated.Count > top.Value)
            {
                aggregated = aggregated.Take(top.Value).ToList();
            }

            if (aggregated.Count == 0)
                return Array.Empty<LeaderboardEntryDto>();

            var userIds = aggregated
                .Select(x => x.UserId)
                .Distinct()
                .ToArray();

            var users = await _db.Users
                .Where(u => userIds.Contains(u.Id))
                .ToListAsync();
            var userMap = users.ToDictionary(u => u.Id);

            // Предварительно загружаем все бейджи для выбранных пользователей
            var userBadges = await _db.UserBadges
                .Where(ub => userIds.Contains(ub.UserId))
                .Include(ub => ub.Badge)
                .ToListAsync();

            var badgesMap = userBadges
                .GroupBy(ub => ub.UserId)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .Select(ub => new BadgeDto
                        {
                            Id = ub.Badge.Id,
                            Name = ub.Badge.Name,
                            ImageUrl = ub.Badge.ImageUrl,
                            Description = ub.Badge.Description
                        })
                        .ToList());

            var result = new List<LeaderboardEntryDto>();

            // формируем финальный список и учитываем ShowInLeaderboard
            for (var i = 0; i < aggregated.Count; i++)
            {
                var row = aggregated[i];
                if (!userMap.TryGetValue(row.UserId, out var user))
                    continue;

                var extra = ParseExtra(user.AdditionalDataJson);
                if (!extra.ShowInLeaderboard)
                    continue;

                var displayName = BuildDisplayName(user);

                // получаем список бейджей, если есть
                badgesMap.TryGetValue(row.UserId, out var badgeList);

                var entry = new LeaderboardEntryDto
                {
                    UserId = user.Id,
                    Rank = i + 1,
                    DisplayName = displayName,
                    Email = user.Email,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Solved = row.SolvedAssignments,
                    SolvedAssignments = row.SolvedAssignments,
                    TotalAttempts = row.TotalAttempts,
                    LastSubmitAt = row.LastSubmitAt,
                    AvatarUrl = user.ProfilePictureUrl,
                    Location = extra.Location,
                    Education = extra.Education,
                    // 👇 тут был косяк: List<BadgeDto> → List<string>
                    Badges = (badgeList ?? new List<BadgeDto>())
                        .Select(b => b.Name)
                        .ToList()
                };

                result.Add(entry);
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

            // загружаем бейджи пользователя
            var userBadges = await _db.UserBadges
                .Where(ub => ub.UserId == userId)
                .Include(ub => ub.Badge)
                .ToListAsync();
            var badgeDtos = userBadges.Select(ub => new BadgeDto
            {
                Id = ub.Badge.Id,
                Name = ub.Badge.Name,
                ImageUrl = ub.Badge.ImageUrl,
                Description = ub.Badge.Description
            }).ToList();

            return new PublicUserProfileDto
            {
                Id = user.Id,
                DisplayName = BuildDisplayName(user),
                Email = user.Email,
                AvatarUrl = user.ProfilePictureUrl,
                Bio = extra.Bio,
                Location = extra.Location,
                Education = extra.Education,
                // Если список навыков не задан, возвращаем пустой список, чтобы избежать null на фронте
                Skills = extra.Skills ?? new List<string>(),
                // Ссылки могут отсутствовать в JSON, поэтому используем null-пропагацию
                Github = extra.Links?.Github,
                Telegram = extra.Links?.Telegram,
                Website = extra.Links?.Website,
                Badges = badgeDtos,
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
                // Use case-insensitive property names to allow camelCase JSON to be deserialized
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                return JsonSerializer.Deserialize<UserProfileExtra>(json, options) ?? new UserProfileExtra();
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
