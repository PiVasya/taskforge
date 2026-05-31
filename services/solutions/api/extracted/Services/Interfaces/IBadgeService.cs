using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    /// <summary>
    /// Сервис для управления бейджами.
    /// Позволяет:
    /// - получать список всех существующих бейджей;
    /// - получать бейджи конкретного пользователя;
    /// - создавать новые бейджи по SVG-файлу;
    /// - назначать бейджи пользователям;
    /// - удалять бейджи (вместе с привязками и, при необходимости, файлами).
    /// </summary>
    public interface IBadgeService
    {
        /// <summary>
        /// Возвращает список всех бейджей.
        /// </summary>
        Task<IReadOnlyList<BadgeDto>> GetAllBadgesAsync();

        /// <summary>
        /// Возвращает бейджи конкретного пользователя.
        /// </summary>
        /// <param name="userId">ID пользователя.</param>
        Task<IReadOnlyList<BadgeDto>> GetUserBadgesAsync(Guid userId);

        /// <summary>
        /// Создаёт новый бейдж по загруженному SVG-файлу.
        /// Сохраняет файл на диск (в wwwroot/badges),
        /// а в базу — относительный путь или data URI.
        /// На выход отдаёт DTO с уже готовым ImageUrl (обычно — data URI).
        /// </summary>
        /// <param name="name">Название бейджа.</param>
        /// <param name="description">Необязательное описание.</param>
        /// <param name="svgFile">Файл изображения (SVG).</param>
        Task<BadgeDto> CreateBadgeAsync(string name, string? description, IFormFile svgFile);

        /// <summary>
        /// Назначает существующий бейдж пользователю. Повторно назначать
        /// один и тот же бейдж одному пользователю не допускается.
        /// </summary>
        /// <param name="userId">ID пользователя.</param>
        /// <param name="badgeId">ID бейджа.</param>
        Task AwardBadgeAsync(Guid userId, Guid badgeId);

        /// <summary>
        /// Снимает (удаляет) назначенный бейдж у пользователя.
        /// Если у пользователя нет такого бейджа — метод ничего не делает.
        /// </summary>
        /// <param name="userId">ID пользователя.</param>
        /// <param name="badgeId">ID бейджа.</param>
        Task RevokeBadgeAsync(Guid userId, Guid badgeId);

        /// <summary>
        /// Удаляет бейдж и все его назначения пользователям.
        /// Если изображение хранится как файл, файл также удаляется.
        /// </summary>
        /// <param name="badgeId">ID бейджа.</param>
        Task DeleteBadgeAsync(Guid badgeId);
    }
}
