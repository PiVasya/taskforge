using System;

namespace taskforge.Data.Models.Profile
{
    /// <summary>
    /// Дополнительные ссылки пользователя в профиле. Содержит URL на GitHub,
    /// ссылку/ник Telegram и персональный веб‑сайт или портфолио. Все свойства
    /// допускают значение null, если пользователь не указал соответствующую ссылку.
    /// </summary>
    using System.Text.Json.Serialization;

    public sealed class UserProfileLinks
    {
        /// <summary>
        /// Ссылка на GitHub‑профиль пользователя. Null, если не указана.
        /// </summary>
        [JsonPropertyName("github")]
        public string? Github { get; set; }

        /// <summary>
        /// Ник или ссылка на Telegram‑аккаунт пользователя. Null, если не указана.
        /// </summary>
        [JsonPropertyName("telegram")]
        public string? Telegram { get; set; }

        /// <summary>
        /// Ссылка на личный сайт или портфолио пользователя. Null, если не указана.
        /// </summary>
        [JsonPropertyName("website")]
        public string? Website { get; set; }
    }
}