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
            return await _db.Badges
                .OrderBy(b => b.Name)
                .Select(b => new BadgeDto
                {
                    Id = b.Id,
                    Name = b.Name,
                    ImageUrl = b.ImageUrl,
                    Description = b.Description
                })
                .ToListAsync();
        }

        public async Task<IReadOnlyList<BadgeDto>> GetUserBadgesAsync(Guid userId)
        {
            return await _db.UserBadges
                .Where(ub => ub.UserId == userId)
                .Include(ub => ub.Badge)
                .OrderBy(ub => ub.AwardedAt)
                .Select(ub => new BadgeDto
                {
                    Id = ub.Badge.Id,
                    Name = ub.Badge.Name,
                    ImageUrl = ub.Badge.ImageUrl,
                    Description = ub.Badge.Description
                })
                .ToListAsync();
        }

        public async Task<BadgeDto> CreateBadgeAsync(string name, string description, IFormFile svgFile)
        {
            if (svgFile == null || svgFile.Length == 0)
                throw new ArgumentException("Файл изображения не выбран");

            var ext = Path.GetExtension(svgFile.FileName)?.ToLowerInvariant();
            if (ext != ".svg")
                throw new InvalidOperationException("Разрешены только SVG-файлы");

            var badgeId = Guid.NewGuid();
            var fileName = $"{badgeId}{ext}";

            // Путь до папки wwwroot/badges. Если wwwroot не существует, используем текущий каталог.
            var webRoot = _env.WebRootPath;
            if (string.IsNullOrEmpty(webRoot))
            {
                webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            }
            var badgeDir = Path.Combine(webRoot, "badges");
            Directory.CreateDirectory(badgeDir);
            var filePath = Path.Combine(badgeDir, fileName);

            // сохраняем файл на диск
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await svgFile.CopyToAsync(stream);
            }

            var badge = new Badge
            {
                Id = badgeId,
                Name = name.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                ImageUrl = $"/badges/{fileName}",
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