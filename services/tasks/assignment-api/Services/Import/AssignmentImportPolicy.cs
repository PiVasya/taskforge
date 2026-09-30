using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Services.Import;

internal static class AssignmentImportPolicy
{
    internal const string TypeImmutableMessage =
        "Тип существующего задания нельзя менять. Создайте новое задание с новым id.";

    internal const string SqlTypeImmutableMessage = TypeImmutableMessage;

    internal const string SqlDeleteRetainedMessage =
        "SQL-задание с сохранённой историей выполнения нельзя удалить. Скройте его через isVisible=false.";

    internal static bool IsImmutableTypeChange(string? currentType, string? requestedType)
    {
        if (string.IsNullOrWhiteSpace(requestedType)) return false;
        if (!TryNormalizeAssignmentType(currentType, out var current)) return false;
        if (!TryNormalizeAssignmentType(requestedType, out var requested)) return false;
        return !string.Equals(current, requested, StringComparison.Ordinal);
    }

    internal static bool IsImmutableSqlTypeChange(string? currentType, string? requestedType)
        => IsImmutableTypeChange(currentType, requestedType)
           && (NormalizeAssignmentType(currentType) == "sql-test" || NormalizeAssignmentType(requestedType) == "sql-test");
}
