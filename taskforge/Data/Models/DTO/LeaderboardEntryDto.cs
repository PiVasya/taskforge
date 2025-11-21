// taskforge/Data/Models/DTO/LeaderboardEntryDto.cs
using System;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// Универсальный DTO для топов:
    /// - публичный топ (LeaderboardController)
    /// - админский топ решений (SolutionAdminService)
    /// </summary>
    public sealed class LeaderboardEntryDto
    {
        public Guid UserId { get; set; }

        /// <summary>Позиция в общем топе (для публичного рейтинга).</summary>
        public int Rank { get; set; }

        /// <summary>Отображаемое имя (Имя + Фамилия или email).</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Email пользователя.</summary>
        public string Email { get; set; } = string.Empty;

        // ---- Старые поля, которые использует SolutionAdminService ----

        /// <summary>Имя (для админского топа решений).</summary>
        public string FirstName { get; set; } = string.Empty;

        /// <summary>Фамилия (для админского топа решений).</summary>
        public string LastName { get; set; } = string.Empty;

        /// <summary>
        /// Число решённых заданий.
        /// В публичном топе это дублирует SolvedAssignments.
        /// В админском топе (GetLeaderboardAsync в SolutionAdminService) заполняется только это поле.
        /// </summary>
        public int Solved { get; set; }

        // ---- Публичный топ ----

        /// <summary>Число решённых заданий (публичный топ).</summary>
        public int SolvedAssignments { get; set; }

        /// <summary>Число всех отправок решений.</summary>
        public int TotalAttempts { get; set; }

        /// <summary>Дата последней отправки.</summary>
        public DateTime? LastSubmitAt { get; set; }

        /// <summary>Аватар пользователя (если есть).</summary>
        public string? AvatarUrl { get; set; }

        /// <summary>Локация (город / вуз и т.п.).</summary>
        public string? Location { get; set; }

        /// <summary>Образование / группа.</summary>
        public string? Education { get; set; }
    }
}
