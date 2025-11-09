using System;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// Короткая информация о пользователе для поиска в админке.
    /// </summary>
    public sealed class UserShortDto
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
    }
}
