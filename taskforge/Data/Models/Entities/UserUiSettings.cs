using System;

namespace taskforge.Data.Models.Entities;

/// <summary>
/// Пользовательские настройки внешнего вида UI (тема/эффекты).
/// Храним отдельно от профиля, чтобы можно было расширять.
/// </summary>
public class UserUiSettings
{
    public int Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string ColorTheme { get; set; } = "blue"; // blue|pink|apple
    public string Mode { get; set; } = "light";      // light|dark

    public bool BgFx { get; set; } = false;
    public string FxMode { get; set; } = "random";   // random|fixed
    public int FxVariant { get; set; } = 3;           // 0..3

    /// <summary>
    /// Стиль страницы решения задач с кодом.
    /// split = как сейчас (условие слева, редактор справа)
    /// editorTop = редактор сверху на всю ширину, условие снизу
    /// </summary>
    public string CodeSolveLayout { get; set; } = "split";

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
