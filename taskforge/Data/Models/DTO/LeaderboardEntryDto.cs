using System;
using System.Collections.Generic;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// DTO строки в топе (лидерборде). Используется как в публичном рейтинге,
    /// так и в админском просмотре решений. Содержит основную информацию о
    /// пользователе и список его бейджей.
    /// </summary>
    public sealed class LeaderboardEntryDto
    {
        public Guid UserId { get; set; }

        /// <summary>
        /// Позиция пользователя в общем топе. Для админских рейтингов может быть
        /// равна нулю.
        /// </summary>
        public int Rank { get; set; }

        /// <summary>Отображаемое имя пользователя (имя + фамилия или email).</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Email пользователя.</summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>Имя (используется в админском рейтинге).</summary>
        public string FirstName { get; set; } = string.Empty;

        /// <summary>Фамилия (используется в админском рейтинге).</summary>
        public string LastName { get; set; } = string.Empty;

        /// <summary>
        /// Количество решённых заданий. В публичном топе совпадает с
        /// SolvedAssignments; в админском рейтинге используется это поле.
        /// </summary>
        public int Solved { get; set; }

        /// <summary>Количество решённых заданий (публичный рейтинг).</summary>
        public int SolvedAssignments { get; set; }

        /// <summary>Суммарный рейтинг (вес) решённых заданий. Используется для сортировки в топе.</summary>
        public int Score { get; set; }

        /// <summary>Общее количество отправок решений.</summary>
        public int TotalAttempts { get; set; }

        /// <summary>Дата последней отправки решения.</summary>
        public DateTime? LastSubmitAt { get; set; }

        /// <summary>URL аватара пользователя. Может быть null.</summary>
        public string? AvatarUrl { get; set; }

        /// <summary>Город / место учёбы пользователя.</summary>
        public string? Location { get; set; }

        /// <summary>Образование / группа пользователя.</summary>
        public string? Education { get; set; }

        /// <summary>
        /// Список бейджей пользователя. Каждый элемент содержит название,
        /// ссылку на изображение (data URI) и описание. Если у пользователя
        /// нет бейджей, список будет пустым.
        /// </summary>
        public List<BadgeDto> Badges { get; set; } = new();
    }
}