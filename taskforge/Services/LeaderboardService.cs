using System;
using System.Collections.Generic;
using System.IO;
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
    /// Добавлена поддержка фильтров и бейджей.
    /// </summary>
    public sealed class LeaderboardService : ILeaderboardService
    {
        private readonly ApplicationDbContext _db;

        public LeaderboardService(ApplicationDbContext db)
        {
            _db = db;
        }

        /// <inheritdoc/>
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

            // загружаем все записи UserBadge для участвующих пользователей без навигации Badge.
            // Использование Include(ub => ub.Badge) может привести к NullReference из‑за
            // теневых свойств BadgeId1/UserId1, поэтому делаем join вручную.
            var userBadges = await _db.UserBadges
                .Where(ub => userIds.Contains(ub.UserId))
                .ToListAsync();

            // получим все уникальные идентификаторы бейджей и загрузим их
            var allBadgeIds = userBadges.Select(ub => ub.BadgeId).Distinct().ToArray();
            var badgeDict = await _db.Badges
                .Where(b => allBadgeIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id);

            // группируем бейджи по пользователю, используя заранее загруженный словарь
            var badgeMap = userBadges
                .GroupBy(ub => ub.UserId)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .Where(ub => badgeDict.ContainsKey(ub.BadgeId))
                        .Select(ub => badgeDict[ub.BadgeId])
                        .Select(b => new BadgeDto
                        {
                            Id = b.Id,
                            Name = b.Name,
                            // Конвертируем путь к файлу в data URI. Если ImageUrl
                            // уже содержит data URI или внешний URL, метод
                            // вернёт оригинальную строку.
                            ImageUrl = ConvertImageUrl(b.ImageUrl),
                            Description = b.Description
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
                // пробуем найти бейджи для пользователя
                badgeMap.TryGetValue(user.Id, out var badges);

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
                    Badges = badges ?? new List<BadgeDto>()
                };
                result.Add(entry);
            }

            return result;
        }

        /// <inheritdoc/>
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

            // загружаем все записи UserBadge пользователя без навигации Badge,
            // затем вручную джойним с таблицей Badges. Это предотвращает NullReference
            // из‑за теневых свойств (BadgeId1/UserId1).
            var userBadges = await _db.UserBadges
                .Where(ub => ub.UserId == userId)
                .ToListAsync();
            var badgeIdsForUser = userBadges.Select(ub => ub.BadgeId).Distinct().ToArray();
            var badgeDictForUser = await _db.Badges
                .Where(b => badgeIdsForUser.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id);
            var badgeDtos = userBadges
                .Where(ub => badgeDictForUser.ContainsKey(ub.BadgeId))
                .Select(ub => badgeDictForUser[ub.BadgeId])
                .Select(b => new BadgeDto
                {
                    Id = b.Id,
                    Name = b.Name,
                    ImageUrl = ConvertImageUrl(b.ImageUrl),
                    Description = b.Description
                })
                .ToList();

            return new PublicUserProfileDto
            {
                Id = user.Id,
                DisplayName = BuildDisplayName(user),
                Email = user.Email,
                AvatarUrl = user.ProfilePictureUrl,
                Bio = extra.Bio,
                Location = extra.Location,
                Education = extra.Education,
                Skills = extra.Skills ?? new List<string>(),
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
                // Используем регистронезависимый десериализатор, чтобы корректно
                // обрабатывать camelCase-поля в JSON (bio, links, skills и т.п.).
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
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

        /// <summary>
        /// Преобразует ссылку на изображение бейджа в data URI. Если ссылка
        /// уже содержит data URI (начинается с "data:image"), метод
        /// возвращает её без изменений. Если ссылка указывает на файл в
        /// каталоге /badges, файл читается из каталога wwwroot и
        /// преобразуется в строку base64. В остальных случаях исходная
        /// строка возвращается без изменений. Этот метод синхронен и
        /// используется в рейтинге и публичном профиле, чтобы SVG‑иконки
        /// корректно отображались даже когда статические файлы недоступны.
        /// </summary>
        /// <param name="imageUrl">Строка из базы данных (может быть null).</param>
        /// <returns>data URI либо исходная строка.</returns>
        private static string ConvertImageUrl(string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return string.Empty;
            // Уже data URI
            if (imageUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
                return imageUrl;
            // Путь в каталоге /badges
            if (imageUrl.StartsWith("/badges/", StringComparison.OrdinalIgnoreCase))
            {
                // Пытаемся вычислить путь к файлу относительно wwwroot
                var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                var relative = imageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var filePath = Path.Combine(webRoot, relative);
                if (File.Exists(filePath))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(filePath);
                        var base64 = Convert.ToBase64String(bytes);
                        return $"data:image/svg+xml;base64,{base64}";
                    }
                    catch
                    {
                        // ignore read errors and fall through
                    }
                }
            }
            return imageUrl;
        }
    }
}