using taskforge.Data.Models.DTO.AI;

namespace taskforge.Services.AI;

internal static class AiGenerationScenarioPromptAdapter
{
    public static string RewritePrompt(string prompt, AiFoundryChatMemoryDto memory, int count)
    {
        var profile = AiGenerationScenarioRouter.Resolve(memory, prompt, null, count);
        var courseTitle = ResolveCourseTitle(memory);
        var concept = AiLadderScenarioSupport.ExtractLearningConcept(memory, prompt, null);
        var conceptClause = BuildConceptClause(concept, "Тема лесенки: ", ".");
        var seriesConceptClause = BuildConceptClause(concept, " Серия должна учить теме: ", ".");

        return profile.Id switch
        {
            "guided-onboarding-ladder" =>
                $"Сгенерируй {count} задач-обучалок для курса «{courseTitle}» в формате friendly walkthrough, как первое вводное задание." +
                conceptClause +
                " Это не просто серия с ростом сложности: каждое условие должно иметь дружелюбное вступление, блок «Следуй шагам:», короткие нумерованные шаги, пояснения в скобках и финальную фразу про запуск/видимый результат.",

            "step-by-step-ladder" =>
                $"Сгенерируй {count} задач для курса «{courseTitle}» в формате очень понятной пошаговой лесенки." +
                conceptClause +
                " Каждое следующее задание должно быть лишь немного сложнее предыдущего, без резких скачков, а подача должна быть дружелюбной и обучающей.",

            "micro-program-series" =>
                $"Сгенерируй {count} маленьких учебных задач для курса «{courseTitle}»." +
                seriesConceptClause +
                " Нужна серия самостоятельных мини-программ с очень маленьким шагом сложности, дружелюбным guided-intro тоном и без олимпиадной сухости.",

            "single-deep-task" =>
                $"Сгенерируй одну сильную цельную задачу для курса «{courseTitle}». Не дроби её в лесенку и не разжёвывай лишние шаги: нужен осмысленный challenge в стиле курса.",

            "course-gap-audit" =>
                $"Сгенерируй задачи для курса «{courseTitle}» так, чтобы они закрывали реальные пробелы курса, а не дублировали уже покрытые шаги.",

            "pretopic-bridges" =>
                $"Сгенерируй {count} подводящих задач для курса «{courseTitle}», которые мягко ведут к следующей теме без резкого скачка сложности.",

            "russian-language-tests" =>
                $"Сгенерируй {count} тестовых заданий по русскому языку с понятной формулировкой, однозначной проверкой и аккуратным уровнем сложности.",

            _ => prompt,
        };
    }

    public static string RewriteSourceText(string? sourceText, AiFoundryChatMemoryDto memory, int count)
    {
        var profile = AiGenerationScenarioRouter.Resolve(memory, null, sourceText, count);
        var concept = AiLadderScenarioSupport.ExtractLearningConcept(memory, null, sourceText);
        var intro = profile.Id switch
        {
            "guided-onboarding-ladder" =>
                $"Пользователь просит обучающую лесенку из {count} задач в жанре friendly walkthrough." +
                BuildConceptClause(concept, " Она должна мягко научить теме «", "».") +
                " Сохраняй scaffold: вступление, «Следуй шагам:», 3-6 шагов, пояснения в скобках, финал про запуск.",

            "micro-program-series" =>
                $"Пользователь просит серию из {count} маленьких самостоятельных учебных задач." +
                BuildConceptClause(concept, " Они должны учить теме «", "»."),

            "step-by-step-ladder" =>
                $"Пользователь хочет лесенку из {count} шагов с очень плавным ростом сложности." +
                BuildConceptClause(concept, " Тема лесенки: «", "»."),

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

        return string.IsNullOrWhiteSpace(profile.DefaultBatchMode)
            ? "topic-pack"
            : profile.DefaultBatchMode;
    }

    public static string SuggestTitleHint(AiFoundryChatMemoryDto memory, string prompt, string? sourceText, int requestedCount, string? currentTitleHint)
    {
        if (!string.IsNullOrWhiteSpace(currentTitleHint))
            return currentTitleHint!;

        var profile = AiGenerationScenarioRouter.Resolve(memory, prompt, sourceText, requestedCount);
        var concept = AiLadderScenarioSupport.ExtractLearningConcept(memory, prompt, sourceText);

        return profile.Id switch
        {
            "guided-onboarding-ladder" => AiLadderScenarioSupport.BuildTitleHint(concept, currentTitleHint, 1, requestedCount),
            "micro-program-series" => AiLadderScenarioSupport.BuildTitleHint(concept, currentTitleHint, 1, requestedCount),
            "step-by-step-ladder" => AiLadderScenarioSupport.BuildTitleHint(concept, currentTitleHint, 1, requestedCount),
            "single-deep-task" => "Сильная задача",
            "russian-language-tests" => "Тест по русскому языку",
            _ => currentTitleHint ?? string.Empty,
        };
    }

    private static string ResolveCourseTitle(AiFoundryChatMemoryDto memory)
    {
        return memory.LastCourseInspection?.CourseTitle
            ?? memory.LastCourseAudit?.CourseTitle
            ?? memory.LastBridgePlan?.CourseTitle
            ?? "курс";
    }

    private static string BuildConceptClause(string? concept, string prefix, string suffix)
    {
        return string.IsNullOrWhiteSpace(concept)
            ? string.Empty
            : prefix + concept.Trim() + suffix;
    }
}
