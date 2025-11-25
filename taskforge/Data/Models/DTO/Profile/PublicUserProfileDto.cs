using System;
using System.Collections.Generic;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// DTO публичного профиля пользователя, который отображается в общем
    /// рейтинге и на странице профиля. Дополнено списком бейджей.
    /// </summary>
    public sealed class PublicUserProfileDto
    {
        public Guid Id { get; set; }

        /// <summary>Отображаемое имя (Имя + Фамилия или email).</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Email пользователя.</summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>Ссылка на аватар (или null, если не задан).</summary>
        public string? AvatarUrl { get; set; }

        /// <summary>Краткое описание пользователя.</summary>
        public string? Bio { get; set; }

        /// <summary>Локация (город / вуз и т.п.).</summary>
        public string? Location { get; set; }

        /// <summary>Образование / группа.</summary>
        public string? Education { get; set; }

        /// <summary>Навыки пользователя (может быть пустой список).</summary>
        public List<string> Skills { get; set; } = new();

        /// <summary>Ссылка на GitHub‑аккаунт пользователя.</summary>
        public string? Github { get; set; }

        /// <summary>Ник или ссылка на Telegram‑аккаунт пользователя.</summary>
        public string? Telegram { get; set; }

        /// <summary>Ссылка на личный сайт или портфолио пользователя.</summary>
        public string? Website { get; set; }

        /// <summary>Позиция пользователя в общем рейтинге.</summary>
        public int Rank { get; set; }

        /// <summary>Количество решённых заданий.</summary>
        public int SolvedAssignments { get; set; }

        /// <summary>Количество всех отправок решений.</summary>
        public int TotalAttempts { get; set; }

        /// <summary>
        /// Список бейджей пользователя. Каждый бейдж содержит название, ссылку на
        /// изображение и описание. Если у пользователя нет бейджей, список пуст.
        /// </summary>
        public List<BadgeDto> Badges { get; set; } = new();
    }
}