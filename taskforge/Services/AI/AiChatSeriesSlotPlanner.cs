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
            var slotPrompt = BuildSlotPrompt(prompt, sourceText, concept, i, count, descriptor);
            var slotSourceText = BuildSlotSourceText(prompt, sourceText, concept, i, count, descriptor);
            var slotTitleHint = AiLadderScenarioSupport.BuildTitleHint(concept, titleHint, i, count);
            var slotDifficulty = Math.Clamp(baseDifficulty + ((i >= Math.Max(3, count)) ? 1 : 0), 1, 5);
            slots.Add(new AiChatSeriesSlotPlan(i, count, slotPrompt, slotSourceText, slotTitleHint, slotDifficulty));
        }
        return slots;
    }

    private static string BuildSlotPrompt(string prompt, string? sourceText, string? concept, int index, int totalCount, string descriptor)
    {
        var basePrompt = string.IsNullOrWhiteSpace(prompt) ? "Сгенерируй учебную задачу." : prompt.Trim();
        var conceptLine = string.IsNullOrWhiteSpace(concept) ? string.Empty : $" Тема этого шага: {concept}.";
        var hardRules = BuildSlotHardRules(prompt, sourceText, concept);
        return $"{basePrompt}\n\nЭто отдельная задача из серии {index}/{totalCount}. Сфокусируйся только на этом шаге прогрессии: {descriptor}.{conceptLine} Следуй стилю дружелюбного первого учебного задания: короткое вступление, отдельный блок «Следуй шагам:», обычно 3-6 нумерованных шагов, маленькие пояснения в скобках, финальная фраза о том, что ученик увидит после запуска. Заголовок и первая фраза должны звучать тепло и по-человечески. Не делай batch, не описывай серию целиком, не ссылайся на другие элементы серии и не дублируй соседние шаги.{hardRules}";
    }

    private static string? BuildSlotSourceText(string? prompt, string? sourceText, string? concept, int index, int totalCount, string descriptor)
    {
        var conceptLine = string.IsNullOrWhiteSpace(concept) ? string.Empty : $" Точная тема шага: «{concept}».";
        var hardRules = BuildSlotHardRules(prompt, sourceText, concept);
        var intro = $"Это шаг {index} из {totalCount}. Нужна только одна самостоятельная задача. Точная роль шага: {descriptor}.{conceptLine} Сохраняй стиль очень понятного учебного walkthrough: дружелюбное вступление, отдельную строку «Следуй шагам:», обычно 3-6 конкретных шагов, маленькие пояснения в скобках и финальную строку про запуск/видимый результат, без сухого олимпиадного тона.{hardRules}";
        if (string.IsNullOrWhiteSpace(sourceText))
            return intro;
        return intro + "\n\n" + sourceText.Trim();
    }

    private static string BuildSlotHardRules(string? prompt, string? sourceText, string? concept)
    {
        var hay = $"{prompt}\n{sourceText}\n{concept}";
        var low = hay.ToLowerInvariant();
        var rules = new List<string>();
        if (low.Contains("c++") || low.Contains("с++") || low.Contains("cpp") || low.Contains("cout") || low.Contains("cin") || low.Contains("#include"))
            rules.Add(" Жёсткое правило языка: курс C++, поэтому не используй Python-синтаксис int(input()), input(), print(), elif, True/False, двоеточия и правила отступов Python; пиши формулировки под C++: cin/cout, фигурные скобки, точка с запятой, else if.");

        var preIf = low.Contains("перед if")
            || low.Contains("перед первым if")
            || low.Contains("до if")
            || low.Contains("до первого if")
            || low.Contains("перед первым появлением if")
            || low.Contains("до первого появления if");
        if (preIf)
            rules.Add(" Жёсткое правило placement/смысла: это подготовка ДО первого if, поэтому в этом слоте нельзя вводить сам if/else/elif и нельзя делать раннее обучение ветвлению; тренируй только базу перед ним — ввод/вывод, сравнения, остаток, булевы результаты 1/0 и граничные значения.");

        return rules.Count == 0 ? string.Empty : " " + string.Join(" ", rules);
    }
}
