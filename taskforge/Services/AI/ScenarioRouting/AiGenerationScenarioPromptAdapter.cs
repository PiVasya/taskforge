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

        var concept = AiLadderScenarioSupport.ExtractLearningConcept(memory, prompt, null);
        var styleContract = AiLadderScenarioSupport.BuildStyleContract(concept, count);
        return profile.Id switch
        {
            "step-by-step-ladder" => $"Сгенерируй {count} задач для курса «{courseTitle}» в формате очень понятной пошаговой лесенки.{(string.IsNullOrWhiteSpace(concept) ? string.Empty : $" Тема лесенки: {concept}.")} Каждое следующее задание должно быть лишь немного сложнее предыдущего, без резких скачков, а подача должна быть дружелюбной и обучающей.

{styleContract}",
            "micro-program-series" => $"Сгенерируй {count} маленьких учебных задач для курса «{courseTitle}».{(string.IsNullOrWhiteSpace(concept) ? string.Empty : $" Серия должна учить теме: {concept}.")} Нужна серия самостоятельных мини-программ с очень маленьким шагом сложности, дружелюбным guided-intro тоном и без олимпиадной сухости.

{styleContract}",
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
        var concept = AiLadderScenarioSupport.ExtractLearningConcept(memory, null, sourceText);
        var styleContract = AiLadderScenarioSupport.BuildStyleContract(concept, count);
        var intro = profile.Id switch
        {
            "micro-program-series" => $"Пользователь просит серию из {count} маленьких самостоятельных учебных задач.{(string.IsNullOrWhiteSpace(concept) ? string.Empty : $" Они должны учить теме «{concept}».")}

{styleContract}",
            "step-by-step-ladder" => $"Пользователь хочет лесенку из {count} шагов с очень плавным ростом сложности.{(string.IsNullOrWhiteSpace(concept) ? string.Empty : $" Тема лесенки: «{concept}».")}

{styleContract}",
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
        var concept = AiLadderScenarioSupport.ExtractLearningConcept(memory, prompt, sourceText);
        return profile.Id switch
        {
            "micro-program-series" => AiLadderScenarioSupport.BuildTitleHint(concept, currentTitleHint, 1, requestedCount),
            "step-by-step-ladder" => AiLadderScenarioSupport.BuildTitleHint(concept, currentTitleHint, 1, requestedCount),
            "single-deep-task" => "Сильная задача",
            "russian-language-tests" => "Тест по русскому языку",
            _ => currentTitleHint ?? string.Empty,
        };
    }
}
