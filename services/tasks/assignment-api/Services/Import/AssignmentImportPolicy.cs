namespace TaskForge.Tasks.Api.Services.Import;

internal static class AssignmentImportPolicy
{
    internal const string SqlTypeImmutableMessage =
        "Тип существующего SQL-задания нельзя менять через импорт. Создайте новое задание с новым id.";

    internal const string SqlDeleteRetainedMessage =
        "SQL-задание с сохранённой историей выполнения нельзя удалить. Скройте его через isVisible=false.";

    internal static bool IsImmutableSqlTypeChange(string? currentType, string? requestedType)
    {
        var current = (currentType ?? string.Empty).Trim().ToLowerInvariant();
        var requested = (requestedType ?? string.Empty).Trim().ToLowerInvariant();
        return current.Length > 0
            && requested.Length > 0
            && current != requested
            && (current == "sql-test" || requested == "sql-test");
    }
}
