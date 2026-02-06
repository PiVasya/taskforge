namespace taskforge.Constants;

public static class QuotaBuckets
{
    // Единый лимитер: один и тот же bucket используется и для заданий, и для топа.
    public const string Tasks = "tasks";

    // Оставляем константу для обратной совместимости (в коде встречается QuotaBuckets.Top),
    // но фактически это тот же bucket, что и Tasks.
    public const string Top = Tasks;
}
