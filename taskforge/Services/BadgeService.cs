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
    /// Реализация сервиса управления бейджами. Предоставляет методы
    /// для создания, получения и назначения бейджей.
    /// </summary>
    public class BadgeService : IBadgeService
    {
        private readonly ApplicationDbContext _db;
        private readonly IWebHostEnvironment _env;

        public BadgeService(ApplicationDbContext db, IWebHostEnvironment env)
        {
            _db = db;
            _env = env;
        }

        public async Task<IReadOnlyList<BadgeDto>> GetAllBadgesAsync()
        {
            /*
             * When returning badge DTOs to the client we ensure the ImageUrl is always
             * a valid image. Historically badges were stored as files under
             * wwwroot/badges and ImageUrl contained a relative path like
             * "/badges/{id}.svg". In some hosting environments these static files are not
             * served, resulting in 404 responses. To make the frontend independent of
             * static file hosting we convert the SVG on disk into a data URI at
             * runtime. If the ImageUrl already contains a data URI we return it as‑is.
             */
            var badges = await _db.Badges
                .OrderBy(b => b.Name)
                .ToListAsync();
            var result = new List<BadgeDto>();
            foreach (var b in badges)
            {
                result.Add(new BadgeDto
                {
                    Id = b.Id,
                    Name = b.Name,
                    Description = b.Description,
                    ImageUrl = await ConvertImageUrlAsync(b.ImageUrl)
                });
            }
            return result;
        }

        public async Task<IReadOnlyList<BadgeDto>> GetUserBadgesAsync(Guid userId)
        {
            /*
             * Similar to GetAllBadgesAsync we want to return data URIs for badge
             * images so they always render correctly. We therefore hydrate the
             * ImageUrl for each badge before returning it.
             */
            var userBadges = await _db.UserBadges
                .Where(ub => ub.UserId == userId)
                .Include(ub => ub.Badge)
                .OrderBy(ub => ub.AwardedAt)
                .ToListAsync();
            var result = new List<BadgeDto>();
            foreach (var ub in userBadges)
            {
                var badge = ub.Badge;
                result.Add(new BadgeDto
                {
                    Id = badge.Id,
                    Name = badge.Name,
                    Description = badge.Description,
                    ImageUrl = await ConvertImageUrlAsync(badge.ImageUrl)
                });
            }
            return result;
        }

        /// <summary>
        /// Deletes a badge and all its assignments. If the badge references a
        /// file under /badges, the file is also removed from disk.
        /// </summary>
        /// <param name="badgeId">ID of the badge to delete.</param>
        public async Task DeleteBadgeAsync(Guid badgeId)
        {
            var badge = await _db.Badges.FindAsync(badgeId);
            if (badge == null)
                throw new KeyNotFoundException("Бейдж не найден");

            // Remove assignments
            var assignments = _db.UserBadges.Where(ub => ub.BadgeId == badgeId);
            _db.UserBadges.RemoveRange(assignments);

            // Remove file if stored on disk (ImageUrl starting with /badges/)
            if (!string.IsNullOrWhiteSpace(badge.ImageUrl) && badge.ImageUrl.StartsWith("/badges/", StringComparison.OrdinalIgnoreCase))
            {
                var webRoot = _env.WebRootPath;
                if (string.IsNullOrEmpty(webRoot))
                {
                    webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                }
                // convert URL path to file system path
                var relativePath = badge.ImageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var filePath = Path.Combine(webRoot, relativePath);
                if (File.Exists(filePath))
                {
                    try
                    {
                        File.Delete(filePath);
                    }
                    catch
                    {
                        // ignore failures deleting file
                    }
                }
            }
            _db.Badges.Remove(badge);
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Converts the stored ImageUrl to a data URI if it points to a file
        /// under /badges. If it's already a data URI or empty, returns it as is.
        /// </summary>
        /// <param name="imageUrl">The image URL stored in the database.</param>
        /// <returns>A data URI string if applicable, otherwise the original imageUrl.</returns>
        private async Task<string> ConvertImageUrlAsync(string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return string.Empty;
            // Already a data URI
            if (imageUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
                return imageUrl;
            // Only convert if referencing our badges folder
            if (imageUrl.StartsWith("/badges/", StringComparison.OrdinalIgnoreCase))
            {
                var webRoot = _env.WebRootPath;
                if (string.IsNullOrEmpty(webRoot))
                {
                    webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                }
                var relative = imageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var filePath = Path.Combine(webRoot, relative);
                if (File.Exists(filePath))
                {
                    // read file bytes and convert to base64 data URI
                    var bytes = await File.ReadAllBytesAsync(filePath);
                    var base64 = Convert.ToBase64String(bytes);
                    return $"data:image/svg+xml;base64,{base64}";
                }
            }
            return imageUrl;
        }

        public async Task<BadgeDto> CreateBadgeAsync(string name, string description, IFormFile svgFile)
        {
            if (svgFile == null || svgFile.Length == 0)
                throw new ArgumentException("Файл изображения не выбран");

            var ext = Path.GetExtension(svgFile.FileName)?.ToLowerInvariant();
            if (ext != ".svg")
                throw new InvalidOperationException("Разрешены только SVG-файлы");

            // создаём идентификатор и читаем содержимое файла в память
            var badgeId = Guid.NewGuid();

            using var ms = new MemoryStream();
            await svgFile.CopyToAsync(ms);
            var base64 = Convert.ToBase64String(ms.ToArray());
            // Data URI для SVG. Такой формат не требует отдачи статических файлов.
            var dataUrl = $"data:image/svg+xml;base64,{base64}";

            var badge = new Badge
            {
                Id = badgeId,
                Name = name.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                ImageUrl = dataUrl,
                CreatedAt = DateTime.UtcNow
            };

            _db.Badges.Add(badge);
            await _db.SaveChangesAsync();

            return new BadgeDto
            {
                Id = badge.Id,
                Name = badge.Name,
                ImageUrl = badge.ImageUrl,
                Description = badge.Description
            };
        }

        public async Task AwardBadgeAsync(Guid userId, Guid badgeId)
        {
            // проверим, что бейдж существует
            var badge = await _db.Badges.FindAsync(badgeId);
            if (badge == null)
                throw new KeyNotFoundException("Бейдж не найден");

            // проверим, что пользователь существует
            var userExists = await _db.Users.AnyAsync(u => u.Id == userId);
            if (!userExists)
                throw new KeyNotFoundException("Пользователь не найден");

            // если уже есть такая связка, не создаём повторно
            var exists = await _db.UserBadges.AnyAsync(ub => ub.UserId == userId && ub.BadgeId == badgeId);
            if (exists) return;

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
    }
}