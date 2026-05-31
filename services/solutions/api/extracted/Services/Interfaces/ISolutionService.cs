using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    /// <summary>
    /// Приём и проверка решений пользователей.
    /// Отвечает за прогон кода по тестам и сохранение результата, а также
    /// предоставление топ-решений и истории решений текущего пользователя.
    /// </summary>
    public interface ISolutionService
    {
        /// <summary>
        /// Принять решение пользователя для задания, прогнать все тесты (включая скрытые),
        /// сохранить результат и вернуть агрегированную информацию.
        /// </summary>
        /// <param name="assignmentId">Идентификатор задания.</param>
        /// <param name="currentUserId">Идентификатор текущего пользователя.</param>
        /// <param name="request">Код и язык.</param>
        Task<SubmitSolutionResultDto> SubmitAsync(
            Guid assignmentId,
            Guid currentUserId,
            SubmitSolutionRequest request
        );

        /// <summary>
        /// Получить список лучших решений для конкретного задания.
        /// </summary>
        /// <param name="assignmentId">Идентификатор задания.</param>
        /// <param name="count">Максимальное число записей.</param>
        Task<List<TopSolutionDto>> GetTopSolutionsAsync(Guid assignmentId, int count);

        /// <summary>
        /// Получить страницу решений текущего пользователя с возможностью фильтрации по курсу/заданию.
        /// </summary>
        Task<IList<SolutionListItemDto>> GetMySolutionsAsync(
            Guid currentUserId,
            Guid? courseId,
            Guid? assignmentId,
            int skip,
            int take
        );

        /// <summary>
        /// Получить все решения текущего пользователя или за период (без пагинации).
        /// </summary>
        Task<IList<SolutionListItemDto>> GetMySolutionsAllAsync(
            Guid currentUserId,
            Guid? courseId,
            Guid? assignmentId,
            int? days
        );

        /// <summary>
        /// Получить детали конкретного решения текущего пользователя (код, язык и т.д.).
        /// </summary>
        Task<SolutionDetailsDto?> GetMySolutionDetailsAsync(Guid currentUserId, Guid solutionId);
    }
}
