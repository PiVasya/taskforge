using taskforge.Data.Models.DTO.AI;
namespace taskforge.Services.AI;

internal static class AiGenerationScenarioPromptAdapter
{
    public static string RewritePrompt(string prompt, AiFoundryChatMemoryDto memory, int count)
    {
        var profile = AiGenerationScenarioRouter.Resolve(memory, prompt, null, count);
        var courseTitle = memory.LastCourseInspection?.CourseTitle
            ?? memory.LastCourseAudit?.CourseTitle
            ?? memory.LastBridgePlan?.CourseTitle
            ?? "курс";

        return profile.Id switch
        {
            "step-by-step-ladder" => $"Сгенерируй {count} задач для курса «{courseTitle}» в формате чёткой пошаговой лесенки: каждое следующее задание немного сложнее предыдущего, без резких скачков, с дружелюбной course-native подачей.",
            "micro-program-series" when profile.RequireExplicitIf => $"Сгенерируй {count} маленьких учебных code-test задач для курса «{courseTitle}», которые действительно учат пользоваться if. Это серия самостоятельных мини-программ с явным if, от первого простого условия к if/else и простым проверкам, без подмены темы оператором % и выводом 1/0 без условного оператора.",
            "micro-program-series" => $"Сгенерируй {count} маленьких учебных code-test задач для курса «{courseTitle}». Нужна серия самостоятельных мини-программ с очень маленьким шагом сложности, дружелюбным guided-intro тоном и без олимпиадной сухости.",
            "single-deep-task" => $"Сгенерируй одну сильную цельную задачу для курса «{courseTitle}». Не дроби её в лесенку и не разжёвывай лишние шаги: нужен осмысленный challenge в стиле курса.",
            "course-gap-audit" => $"Сгенерируй задачи для курса «{courseTitle}» так, чтобы они закрывали реальные пробелы курса, а не дублировали уже покрытые шаги.",
            "pretopic-bridges" => $"Сгенерируй {count} подводящих задач для курса «{courseTitle}», которые мягко ведут к следующей теме без резкого скачка сложности.",
            "russian-language-tests" => $"Сгенерируй {count} тестовых заданий по русскому языку с понятной формулировкой, однозначной проверкой и аккуратным уровнем сложности.",
            _ => prompt,
        };
    }

    public static string RewriteSourceText(string? sourceText, AiFoundryChatMemoryDto memory, int count)
    {
        var profile = AiGenerationScenarioRouter.Resolve(memory, null, sourceText, count);
        var intro = profile.Id switch
        {
            "micro-program-series" when profile.RequireExplicitIf => $"Пользователь просит не подготовительные мостики, а серию из {count} маленьких программ именно на освоение if. Каждая задача должна реально использовать if.",
            "step-by-step-ladder" => $"Пользователь хочет лесенку из {count} шагов с очень плавным ростом сложности.",
            "single-deep-task" => "Пользователь хочет одну более сложную задачу вместо набора микрошагов.",
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(intro))
            return sourceText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceText))
            return intro;
        return intro + "\n\n" + sourceText.Trim();
    }

    public static string DetermineMode(AiFoundryChatMemoryDto memory, string prompt, string? sourceText, int requestedCount)
    {
        var profile = AiGenerationScenarioRouter.Resolve(memory, prompt, sourceText, requestedCount);
        if (requestedCount <= 1 || profile.PreferSingleDeepTask)
            return "single-draft";
        return string.IsNullOrWhiteSpace(profile.DefaultBatchMode) ? "topic-pack" : profile.DefaultBatchMode;
    }

    public static string SuggestTitleHint(AiFoundryChatMemoryDto memory, string prompt, string? sourceText, int requestedCount, string? currentTitleHint)
    {
        if (!string.IsNullOrWhiteSpace(currentTitleHint))
            return currentTitleHint!;

        var profile = AiGenerationScenarioRouter.Resolve(memory, prompt, sourceText, requestedCount);
        return profile.Id switch
        {
            "micro-program-series" when profile.RequireExplicitIf => requestedCount > 1 ? "Первые программы с if" : "Первый if",
            "step-by-step-ladder" => "Пошаговая лесенка",
            "single-deep-task" => "Сильная задача",
            "russian-language-tests" => "Тест по русскому языку",
            _ => currentTitleHint ?? string.Empty,
        };
    }
}
