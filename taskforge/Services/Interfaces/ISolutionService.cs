using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;
// Import DTOs from the canonical namespace. TopSolutionDto is defined in Data.Models.DTO.

namespace taskforge.Services.Interfaces
{
    /// <summary>
    /// Приём и проверка решений пользователей.
    /// Отвечает за прогон кода по тестам и сохранение результата, а также предоставление топ‑решений.
    /// </summary>
    public interface ISolutionService
    {
        /// <summary>
        /// Принять решение пользователя для задания, прогнать все тесты (включая скрытые),
        /// сохранить UserTaskSolution и вернуть разбор по каждому кейсу.
        /// </summary>
        /// <param name="assignmentId">Id задания.</param>
        /// <param name="currentUserId">Id текущего пользователя.</param>
        /// <param name="request">Код и язык.</param>
        /// <returns>Итог проверки с пометкой скрытых тестов.</returns>
        Task<SubmitSolutionResultDto> SubmitAsync(Guid assignmentId, Guid currentUserId, SubmitSolutionRequest request);

        /// <summary>
        /// Получить список лучших решений для конкретного задания. Результаты сортируются
        /// по убыванию количества успешно пройденных тестов, затем по времени отправки.
        /// </summary>
        /// <param name="assignmentId">Идентификатор задания.</param>
        /// <param name="count">Количество решений, которое следует вернуть.</param>
        /// <returns>Список топ‑решений, содержащих информацию о пользователе и коде.</returns>
        Task<List<TopSolutionDto>> GetTopSolutionsAsync(Guid assignmentId, int count);
    }
}
