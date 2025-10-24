using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces;

/// <summary>
/// Интерфейс сервиса администрирования решений пользователей. Дополнен методами для получения полного
/// списка решений и их удаления.
/// </summary>
public interface ISolutionAdminService
{
    /// <summary>
    /// Получение списка решений пользователя с пагинацией.
    /// </summary>
    Task<IList<SolutionListItemDto>> GetByUserAsync(Guid userId, Guid? courseId, Guid? assignmentId, int skip, int take);

    /// <summary>
    /// Получение детальной информации о решении.
    /// </summary>
    Task<SolutionDetailsDto?> GetDetailsAsync(Guid solutionId);

    /// <summary>
    /// Получение лидерборда по количеству решённых заданий.
    /// </summary>
    Task<IList<LeaderboardEntryDto>> GetLeaderboardAsync(Guid? courseId, int? days, int top);

    /// <summary>
    /// Поиск пользователей по email/имени/фамилии.
    /// </summary>
    Task<IList<UserShortDto>> SearchUsersAsync(string query, int take);

    /// <summary>
    /// Получение полного списка решений пользователя. Может быть ограничен только курсом, заданием или
    /// временным диапазоном (days — количество дней с текущего момента). Если days равно null, то
    /// возвращаются все решения за всё время.
    /// </summary>
    Task<IList<SolutionListItemDto>> GetAllByUserAsync(Guid userId, Guid? courseId, Guid? assignmentId, int? days);

    /// <summary>
    /// Удаление решений пользователя. Можно ограничить удаляемые записи по курсу или заданию.
    /// </summary>
    Task DeleteUserSolutionsAsync(Guid userId, Guid? courseId, Guid? assignmentId);
}

/// <summary>
/// DTO краткой информации о пользователе. Оставлено без изменений, но воспроизведено здесь для полноты.
/// </summary>
public sealed class UserShortDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
}