using System;

namespace taskforge.Data.Models.DTO.Solutions
{
    public sealed class JudgeRequestDto
    {
        public Guid AssignmentId { get; set; }
        /// <summary> "csharp" | "cpp" | "python" </summary>
        public string Language { get; set; } = "csharp";
        /// <summary> Исходный код </summary>
        public string Source { get; set; } = string.Empty;
        /// <summary> Доп. stdin (опционально; при тестах обычно не нужен) </summary>
        public string? Stdin { get; set; }
        /// <summary> Лимит времени в мс (опц.) </summary>
        public int? TimeLimitMs { get; set; }
        /// <summary> Лимит памяти в МБ (опц.) </summary>
        public int? MemoryLimitMb { get; set; }
    }
}
