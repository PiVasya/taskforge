using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    /// <summary>
    /// Приём и проверка решений пользователей.
    /// Отвечает за прогон кода по тестам и сохранение результата.
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
    }
}
