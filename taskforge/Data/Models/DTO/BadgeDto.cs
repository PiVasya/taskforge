using System;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// DTO для передачи информации о бейдже между сервером и клиентом.
    /// Содержит идентификатор, название, ссылку на картинку и описание.
    /// </summary>
    public sealed class BadgeDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}