using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    /// <summary>
    /// Реализация сервиса управления бейджами.
    /// Умеет:
    /// - создавать бейджи по SVG-файлам (файл на диск, путь в БД);
    /// - выдавать бейджи пользователям;
    /// - возвращать списки бейджей в виде DTO с корректным ImageUrl (data URI);
    /// - удалять бейджи вместе с связями и при необходимости — с файлами.
    /// </summary>
    public class BadgeService : IBadgeService
    {
        private readonly ApplicationDbContext _db;
        private readonly IWebHostEnvironment _env;

        public BadgeService(ApplicationDbContext db, IWebHostEnvironment env)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _env = env ?? throw new ArgumentNullException(nameof(env));
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<BadgeDto>> GetAllBadgesAsync()
        {
            var badges = await _db.Badges
                .AsNoTracking()
                .OrderBy(b => b.Name)
                .ToListAsync();

            var result = new List<BadgeDto>(badges.Count);

            foreach (var badge in badges)
            {
                var imageUrl = await ConvertImageUrlAsync(badge.ImageUrl);

                result.Add(new BadgeDto
                {
                    Id = badge.Id,
                    Name = badge.Name,
                    Description = badge.Description,
                    ImageUrl = imageUrl
                });
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<BadgeDto>> GetUserBadgesAsync(Guid userId)
        {
            var userBadges = await _db.UserBadges
                .AsNoTracking()
                .Include(ub => ub.Badge)
                .Where(ub => ub.UserId == userId)
                .OrderBy(ub => ub.AwardedAt)
                .ToListAsync();

            var result = new List<BadgeDto>(userBadges.Count);

            foreach (var ub in userBadges)
            {
                if (ub.Badge == null)
                    continue;

                var imageUrl = await ConvertImageUrlAsync(ub.Badge.ImageUrl);

                result.Add(new BadgeDto
                {
                    Id = ub.Badge.Id,
                    Name = ub.Badge.Name,
                    Description = ub.Badge.Description,
                    ImageUrl = imageUrl
                });
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<BadgeDto> CreateBadgeAsync(string name, string? description, IFormFile svgFile)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Имя бейджа не может быть пустым.", nameof(name));

            if (svgFile == null || svgFile.Length == 0)
                throw new ArgumentException("Файл изображения не выбран или пуст.", nameof(svgFile));

            var extension = Path.GetExtension(svgFile.FileName)?.ToLowerInvariant() ?? string.Empty;
            if (extension != ".svg")
                throw new InvalidOperationException("Разрешены только SVG-файлы (.svg).");

            var badgeId = Guid.NewGuid();

            // 1. Папка wwwroot
            var webRoot = _env.WebRootPath;
            if (string.IsNullOrWhiteSpace(webRoot))
            {
                webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            }

            // 2. Папка для бейджей
            var badgesDir = Path.Combine(webRoot, "badges");
            Directory.CreateDirectory(badgesDir);

            // 3. Имя файла
            var fileName = $"{badgeId}{extension}";
            var filePath = Path.Combine(badgesDir, fileName);

            // 4. Сохраняем файл на диск
            await using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                await svgFile.CopyToAsync(fileStream);
            }

            // 5. В БД сохраняем относительный путь
            var imageUrl = $"/badges/{fileName}";

            var badge = new Badge
            {
                Id = badgeId,
                Name = name.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                ImageUrl = imageUrl,
                CreatedAt = DateTime.UtcNow
            };

            _db.Badges.Add(badge);
            await _db.SaveChangesAsync();

            // 6. Для фронта сразу отдаём data URI (или путь, если не удалось прочитать файл)
            var dtoImageUrl = await ConvertImageUrlAsync(imageUrl);

            return new BadgeDto
            {
                Id = badge.Id,
                Name = badge.Name,
                Description = badge.Description,
                ImageUrl = dtoImageUrl
            };
        }

        /// <inheritdoc />
        public async Task DeleteBadgeAsync(Guid badgeId)
        {
            var badge = await _db.Badges.FirstOrDefaultAsync(b => b.Id == badgeId);
            if (badge == null)
            {
                // Ничего не делаем, если бейдж уже удалён.
                return;
            }

            // Попробуем удалить файл, если он хранится как путь.
            if (!string.IsNullOrWhiteSpace(badge.ImageUrl) &&
                !badge.ImageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteBadgeFile(badge.ImageUrl);
            }

            // UserBadge удалятся каскадно, если настроен cascade delete.
            // Даже если нет — можно явно удалить:
            // var links = _db.UserBadges.Where(ub => ub.BadgeId == badgeId);
            // _db.UserBadges.RemoveRange(links);

            _db.Badges.Remove(badge);
            await _db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task AwardBadgeAsync(Guid userId, Guid badgeId)
        {
            // Проверим, что сам бейдж существует.
            var badgeExists = await _db.Badges
                .AsNoTracking()
                .AnyAsync(b => b.Id == badgeId);

            if (!badgeExists)
                throw new InvalidOperationException("Указанный бейдж не существует.");

            // Можно также проверить существование пользователя, если нужно.
            var userExists = await _db.Users
                .AsNoTracking()
                .AnyAsync(u => u.Id == userId);

            if (!userExists)
                throw new InvalidOperationException("Указанный пользователь не существует.");

            // Не даём назначить один и тот же бейдж дважды.
            var alreadyHas = await _db.UserBadges
                .AsNoTracking()
                .AnyAsync(ub => ub.UserId == userId && ub.BadgeId == badgeId);

            if (alreadyHas)
                return;

            var userBadge = new UserBadge
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BadgeId = badgeId,
                AwardedAt = DateTime.UtcNow
            };

            _db.UserBadges.Add(userBadge);
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Снимает (удаляет) указанный бейдж у пользователя.
        /// Если у пользователя нет этого бейджа – метод не делает ничего.
        /// </summary>
        /// <param name="userId">ID пользователя.</param>
        /// <param name="badgeId">ID бейджа.</param>
        public async Task RevokeBadgeAsync(Guid userId, Guid badgeId)
        {
            var userBadge = await _db.UserBadges
                .FirstOrDefaultAsync(ub => ub.UserId == userId && ub.BadgeId == badgeId);

            if (userBadge == null)
            {
                return;
            }

            _db.UserBadges.Remove(userBadge);
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Конвертирует строку ImageUrl в то, что удобно фронту:
        /// - если это уже data URI — возвращаем как есть;
        /// - если это относительный путь ("/badges/xxx.svg") — читаем файл
        ///   и оборачиваем в data URI;
        /// - если файл не найден — возвращаем исходную строку или пустую.
        /// </summary>
        private async Task<string> ConvertImageUrlAsync(string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return string.Empty;

            if (imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return imageUrl;

            // Ожидаем относительный путь вида "/badges/xxx.svg" или "badges/xxx.svg"
            var webRoot = _env.WebRootPath;
            if (string.IsNullOrWhiteSpace(webRoot))
            {
                webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            }

            var relativePath = imageUrl.TrimStart('~').TrimStart('/');
            var fullPath = Path.Combine(webRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                // Файл не найден — пусть фронт сам решает, что делать.
                return imageUrl;
            }

            var bytes = await File.ReadAllBytesAsync(fullPath);
            var base64 = Convert.ToBase64String(bytes);

            var extension = Path.GetExtension(fullPath)?.ToLowerInvariant();
            var mimeType = extension switch
            {
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                _ => "application/octet-stream"
            };

            return $"data:{mimeType};base64,{base64}";
        }

        /// <summary>
        /// Пытается удалить файл изображения бейджа, если он хранится как путь.
        /// Ошибки не пробрасываются наружу — чтобы не ломать удаление в целом.
        /// </summary>
        /// <param name="imageUrl">Относительный URL, например "/badges/xxx.svg".</param>
        private void TryDeleteBadgeFile(string imageUrl)
        {
            try
            {
                var webRoot = _env.WebRootPath;
                if (string.IsNullOrWhiteSpace(webRoot))
                {
                    webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                }

                var relativePath = imageUrl.TrimStart('~').TrimStart('/');
                var fullPath = Path.Combine(webRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch
            {
                // Лог можно добавить при необходимости, но исключение глушим.
            }
        }
    }
}
