using System.Collections.Generic;

namespace taskforge.Data.Models.Profile
{
    /// <summary>
    /// Дополнительные сведения о пользователе, хранящиеся в JSON‑колонке
    /// <see cref="taskforge.Data.Models.Entities.User.AdditionalDataJson"/>. Эти поля
    /// заполняются пользователем на странице профиля и используются в публичном
    /// профиле и топе.
    /// </summary>
    using System.Text.Json.Serialization;

    public sealed class UserProfileExtra
    {
        /// <summary>
        /// Краткое описание о себе. Может быть пустой строкой.
        /// </summary>
        [JsonPropertyName("bio")]
        public string? Bio { get; set; }

        /// <summary>
        /// Город или место учёбы/работы пользователя. Может быть пустой строкой.
        /// </summary>
        [JsonPropertyName("location")]
        public string? Location { get; set; }

        /// <summary>
        /// Образование или группа пользователя. Может быть пустой строкой.
        /// </summary>
        [JsonPropertyName("education")]
        public string? Education { get; set; }

        /// <summary>
        /// Список навыков пользователя. Хранится как массив строк. Если не указан,
        /// будет пустым списком.
        /// </summary>
        [JsonPropertyName("skills")]
        public List<string> Skills { get; set; } = new();

        /// <summary>
        /// Ссылки на внешние профили (GitHub, Telegram, Website). По умолчанию
        /// создаётся пустой объект, чтобы избежать NullReferenceException при
        /// доступе к полям Links.*.
        /// </summary>
        [JsonPropertyName("links")]
        public UserProfileLinks Links { get; set; } = new UserProfileLinks();

        /// <summary>
        /// Флаг, который управляет отображением пользователя в общем рейтинге. Если
        /// false, пользователь не будет включён в рейтинг. По умолчанию true.
        /// </summary>
        [JsonPropertyName("showInLeaderboard")]
        public bool ShowInLeaderboard { get; set; } = true;
    }
}