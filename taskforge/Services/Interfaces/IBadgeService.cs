using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    /// <summary>
    /// Сервис для управления бейджами. Позволяет получать список всех
    /// существующих бейджей, создавать новые бейджи, получать бейджи
    /// конкретного пользователя и назначать бейдж пользователю.
    /// </summary>
    public interface IBadgeService
    {
        /// <summary>
        /// Возвращает список всех доступных бейджей.
        /// </summary>
        Task<IReadOnlyList<BadgeDto>> GetAllBadgesAsync();

        /// <summary>
        /// Возвращает список бейджей пользователя в порядке их назначения.
        /// </summary>
        Task<IReadOnlyList<BadgeDto>> GetUserBadgesAsync(Guid userId);

        /// <summary>
        /// Создаёт новый бейдж. Для картинки используется переданный SVG‑файл.
        /// </summary>
        /// <param name="name">Название бейджа.</param>
        /// <param name="description">Описание (может быть пустым).</param>
        /// <param name="svgFile">SVG‑файл с изображением.</param>
        /// <returns>DTO созданного бейджа.</returns>
        Task<BadgeDto> CreateBadgeAsync(string name, string description, IFormFile svgFile);

        /// <summary>
        /// Назначает существующий бейдж пользователю. Повторно назначать
        /// один и тот же бейдж одному пользователю не допускается.
        /// </summary>
        /// <param name="userId">ID пользователя.</param>
        /// <param name="badgeId">ID бейджа.</param>
        Task AwardBadgeAsync(Guid userId, Guid badgeId);
    }
}