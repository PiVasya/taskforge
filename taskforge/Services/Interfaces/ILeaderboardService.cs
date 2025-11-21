// modified version of ILeaderboardService.cs with support for filtering parameters
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ILeaderboardService
    {
        /// <summary>
        /// Возвращает публичный рейтинг пользователей.
        ///
        /// Опциональные параметры позволяют ограничить выборку по курсу, числу дней,
        /// группе либо максимальному количеству записей. Все параметры необязательны.
        /// Если параметр не указан, используется значение по умолчанию (без фильтра).
        /// </summary>
        /// <param name="courseId">Идентификатор курса для фильтрации (null — все курсы).</param>
        /// <param name="days">Количество последних дней для учёта решений (null — за всё время).</param>
        /// <param name="groupId">Идентификатор группы (зарезервировано для будущих фильтров).</param>
        /// <param name="top">Максимальное количество записей (null — вернуть всех).</param>
        Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(
            Guid? courseId = null,
            int? days = null,
            Guid? groupId = null,
            int? top = null);

        /// <summary>
        /// Возвращает публичный профиль пользователя по его идентификатору.
        /// </summary>
        /// <param name="userId">Идентификатор пользователя.</param>
        Task<PublicUserProfileDto?> GetPublicProfileAsync(Guid userId);
    }
}