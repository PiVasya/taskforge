namespace taskforge.Services.AI;

internal sealed record AiChatSeriesSlotPlan(int Index, int TotalCount, string Prompt, string? SourceText, string? TitleHint, int Difficulty);

internal static class AiChatSeriesSlotPlanner
{
    public static IReadOnlyList<AiChatSeriesSlotPlan> Build(string prompt, string? sourceText, string? titleHint, int count, int baseDifficulty)
    {
        count = Math.Clamp(count, 1, 12);
        var concept = AiLadderScenarioSupport.ExtractLearningConcept(null, prompt, sourceText);
        var slots = new List<AiChatSeriesSlotPlan>();
        for (var i = 1; i <= count; i++)
        {
            var descriptor = AiLadderScenarioSupport.DescribeSlotRole(i, count);
            var slotPrompt = BuildSlotPrompt(prompt, concept, i, count, descriptor);
            var slotSourceText = BuildSlotSourceText(sourceText, concept, i, count, descriptor);
            var slotTitleHint = AiLadderScenarioSupport.BuildTitleHint(concept, titleHint, i, count);
            var slotDifficulty = Math.Clamp(baseDifficulty + ((i >= Math.Max(3, count)) ? 1 : 0), 1, 5);
            slots.Add(new AiChatSeriesSlotPlan(i, count, slotPrompt, slotSourceText, slotTitleHint, slotDifficulty));
        }
        return slots;
    }

    private static string BuildSlotPrompt(string prompt, string? concept, int index, int totalCount, string descriptor)
    {
        var basePrompt = string.IsNullOrWhiteSpace(prompt) ? "Сгенерируй учебную задачу." : prompt.Trim();
        var conceptLine = string.IsNullOrWhiteSpace(concept) ? string.Empty : $" Тема этого шага: {concept}.";
        return $"{basePrompt}\n\nЭто отдельная задача из серии {index}/{totalCount}. Сфокусируйся только на этом шаге прогрессии: {descriptor}.{conceptLine} Следуй стилю дружелюбного первого учебного задания: короткое вступление, отдельный блок «Следуй шагам:», обычно 3-6 нумерованных шагов, маленькие пояснения в скобках, финальная фраза о том, что ученик увидит после запуска. Заголовок и первая фраза должны звучать тепло и по-человечески. Не делай batch, не описывай серию целиком, не ссылайся на другие элементы серии и не дублируй соседние шаги.";
    }

    private static string? BuildSlotSourceText(string? sourceText, string? concept, int index, int totalCount, string descriptor)
    {
        var conceptLine = string.IsNullOrWhiteSpace(concept) ? string.Empty : $" Точная тема шага: «{concept}».";
        var intro = $"Это шаг {index} из {totalCount}. Нужна только одна самостоятельная задача. Точная роль шага: {descriptor}.{conceptLine} Сохраняй стиль очень понятного учебного walkthrough: дружелюбное вступление, отдельную строку «Следуй шагам:», обычно 3-6 конкретных шагов, маленькие пояснения в скобках и финальную строку про запуск/видимый результат, без сухого олимпиадного тона.";
        if (string.IsNullOrWhiteSpace(sourceText))
            return intro;
        return intro + "\n\n" + sourceText.Trim();
    }
}
