using System;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// DTO для передачи сведений о бейдже пользователям и фронтенду. Содержит
    /// идентификатор, название, URL изображения и необязательное описание.
    /// </summary>
    public sealed class BadgeDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}