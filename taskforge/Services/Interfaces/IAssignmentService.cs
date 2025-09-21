using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    /// <summary>
    /// Работа с заданиями внутри курсов: создание, список, детали.
    /// Соблюдаем SRP: только доменная логика заданий, без знаний о HTTP/контроллерах.
    /// </summary>
    public interface IAssignmentService
    {
        /// <summary>
        /// Создать задание в курсе <paramref name="courseId"/>.
        /// </summary>
        /// <param name="courseId">Идентификатор курса.</param>
        /// <param name="request">Поля задания, включая тесты.</param>
        /// <param name="currentUserId">Текущий пользователь (для проверок прав и аудита).</param>
        /// <returns>Id созданного задания.</returns>
        Task<Guid> CreateAsync(Guid courseId, CreateAssignmentRequest request, Guid currentUserId);

        /// <summary>
        /// Получить список заданий курса с признаком, решал ли их текущий пользователь.
        /// </summary>
        /// <param name="courseId">Идентификатор курса.</param>
        /// <param name="currentUserId">Текущий пользователь (для вычисления SolvedByCurrentUser).</param>
        /// <returns>Коллекция элементов списка заданий.</returns>
        Task<IList<AssignmentListItemDto>> GetByCourseAsync(Guid courseId, Guid currentUserId);

        /// <summary>
        /// Получить детальную информацию по заданию.
        /// </summary>
        /// <param name="assignmentId">Идентификатор задания.</param>
        /// <param name="currentUserId">Текущий пользователь (для флага SolvedByCurrentUser).</param>
        /// <returns>Детальная DTO или null, если не найдено/нет доступа.</returns>
        Task<AssignmentDetailsDto?> GetDetailsAsync(Guid assignmentId, Guid currentUserId);

        /// <summary>Полное обновление задания с заменой тестов (только владелец курса).</summary>
        Task UpdateAsync(Guid assignmentId, Guid currentUserId, UpdateAssignmentRequest request);

        /// <summary>Удалить задание (только владелец курса).</summary>
        Task DeleteAsync(Guid assignmentId, Guid currentUserId);
    }
}
