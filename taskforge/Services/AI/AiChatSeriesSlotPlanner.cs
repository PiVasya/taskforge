using System.Text;

namespace taskforge.Services.AI;

internal sealed record AiChatSeriesSlotPlan(int Index, int TotalCount, string Prompt, string? SourceText, string? TitleHint, int Difficulty);

internal static class AiChatSeriesSlotPlanner
{
    public static IReadOnlyList<AiChatSeriesSlotPlan> Build(string prompt, string? sourceText, string? titleHint, int count, int baseDifficulty)
    {
        count = Math.Clamp(count, 1, 12);
        var concept = AiLadderScenarioSupport.ExtractLearningConcept(null, prompt, sourceText);
        var styleContract = AiLadderScenarioSupport.BuildStyleContract(concept, count);
        var slots = new List<AiChatSeriesSlotPlan>();
        for (var i = 1; i <= count; i++)
        {
            var contract = AiLadderScenarioSupport.BuildSlotContract(i, count);
            var slotPrompt = BuildSlotPrompt(prompt, concept, contract, styleContract);
            var slotSourceText = BuildSlotSourceText(sourceText, concept, contract, styleContract);
            var slotTitleHint = AiLadderScenarioSupport.BuildTitleHint(concept, titleHint, i, count);
            var slotDifficulty = ComputeDifficulty(baseDifficulty, contract, count);
            slots.Add(new AiChatSeriesSlotPlan(i, count, slotPrompt, slotSourceText, slotTitleHint, slotDifficulty));
        }
        return slots;
    }

    private static int ComputeDifficulty(int baseDifficulty, AiLadderSlotContract contract, int totalCount)
    {
        var extra = contract.Index <= 2 ? 0 : contract.Index <= Math.Max(3, totalCount - 1) ? 1 : 2;
        return Math.Clamp(baseDifficulty + extra, 1, 5);
    }

    private static string BuildSlotPrompt(string prompt, string? concept, AiLadderSlotContract contract, string styleContract)
    {
        var basePrompt = string.IsNullOrWhiteSpace(prompt) ? "Сгенерируй учебную задачу." : prompt.Trim();
        var conceptLine = string.IsNullOrWhiteSpace(concept) ? string.Empty : $" Тема этого шага: {concept}.";
        return $@"{basePrompt}

Это отдельная задача из серии {contract.Index}/{contract.TotalCount}. Сфокусируйся только на этом шаге прогрессии: {contract.StepRole}.{conceptLine}
Сделай задачу самостоятельной и завершённой. Не описывай серию целиком, не ссылайся на соседние шаги и не повторяй соседние слоты почти дословно.
У этой задачи бюджет новых идей: {contract.NewIdeaBudget}. Резкий скачок сложности запрещён.

{styleContract}
{AiLadderScenarioSupport.BuildSlotContractBlock(contract, concept)}";
    }

    private static string? BuildSlotSourceText(string? sourceText, string? concept, AiLadderSlotContract contract, string styleContract)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Это шаг {contract.Index} из {contract.TotalCount}. Нужна только одна самостоятельная задача.");
        sb.AppendLine($"Роль шага: {contract.StepRole}.");
        sb.AppendLine($"Сложность шага: {contract.ComplexityBand}. Бюджет новых идей: {contract.NewIdeaBudget}.");
        if (!string.IsNullOrWhiteSpace(concept))
            sb.AppendLine($"Точная тема шага: «{concept}».");
        sb.AppendLine("Задача должна выглядеть как очень понятное первое учебное задание: дружелюбное вступление, блок «Следуй шагам:», конкретные шаги, короткие пояснения в скобках, спокойный финал о результате запуска.");
        sb.AppendLine("Не делай сухой олимпиадный statement. Не упоминай другие задачи серии. Не делай вид, что это batch.");
        sb.AppendLine();
        sb.AppendLine(styleContract);
        sb.AppendLine(AiLadderScenarioSupport.BuildSlotContractBlock(contract, concept));
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            sb.AppendLine();
            sb.AppendLine(sourceText.Trim());
        }
        return sb.ToString().Trim();
    }
}
