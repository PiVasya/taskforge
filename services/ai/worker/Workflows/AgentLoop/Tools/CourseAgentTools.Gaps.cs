using System.Text.Json.Nodes;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public static partial class CourseAgentTools
{
    public static JsonObject FindLearningGaps(AgentLoopState state)
    {
        var assignments = GetAssignments(state);
        var findings = new JsonArray();
        var seenConcepts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AssignmentSnapshot? previous = null;

        foreach (var current in assignments)
        {
            if (string.IsNullOrWhiteSpace(current.Description))
            {
                findings.Add(Finding("medium", "Пустое описание задания", current.Title, "Ученик не увидит понятного условия.", "Заполнить student-facing описание: цель, что сделать, пример или шаги."));
            }
            else if (current.Description.Trim().Length < 80)
            {
                findings.Add(Finding("low", "Слишком короткое описание", current.Title, "Описание может быть недостаточным для новичка.", "Добавить цель задания и короткое пояснение, особенно если это обучающая задача."));
            }

            var testCount = current.PublicTestCount + current.HiddenTestCount;
            if (current.Type.Contains("code", StringComparison.OrdinalIgnoreCase) && testCount > 0 && current.HiddenTestCount == 0)
            {
                findings.Add(Finding("medium", "Нет скрытых тестов", current.Title, "Решение можно подогнать под открытые примеры.", "Добавить хотя бы один hidden test, а для задач с вводом — несколько разных случаев."));
            }

            if (current.Type.Contains("code", StringComparison.OrdinalIgnoreCase) && testCount is > 0 and < 3)
            {
                findings.Add(Finding("low", "Мало тестов", current.Title, $"Найдено тестов: {testCount}.", "Увеличить покрытие тестами: обычный случай, крайний случай, отрицательные/нулевые значения, hidden test."));
            }

            if (!current.HasReferenceSolution && current.Type.Contains("code", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding("high", "Нет эталонного решения", current.Title, "AI/платформа не смогут уверенно проверить качество тестов.", "Добавить referenceSolution, проходящий все public/hidden tests."));
            }

            if (previous is not null)
            {
                var ratingJump = current.Rating.GetValueOrDefault(previous.Rating ?? 0) - previous.Rating.GetValueOrDefault(current.Rating ?? 0);
                var difficultyJump = current.Difficulty.GetValueOrDefault(previous.Difficulty ?? 0) - previous.Difficulty.GetValueOrDefault(current.Difficulty ?? 0);
                if (ratingJump >= 25 || difficultyJump >= 2)
                {
                    findings.Add(Finding("high", "Резкий скачок сложности", current.Title, $"Предыдущее задание: {previous.Title}. Скачок rating={ratingJump}, difficulty={difficultyJump}.", "Добавить 1-3 bridge tasks между заданиями, чтобы ввести недостающий навык отдельно."));
                }
            }

            var concepts = ExtractConcepts(current).ToList();
            var newConcepts = concepts.Where(x => !seenConcepts.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (newConcepts.Count >= 3 && current.Index > 1)
            {
                findings.Add(Finding("medium", "Много новых понятий в одном задании", current.Title, "Новые понятия: " + string.Join(", ", newConcepts.Take(6)), "Разбить на несколько маленьких заданий или добавить обучающую лестницу перед этим местом."));
            }
            foreach (var concept in concepts)
                seenConcepts.Add(concept);

            previous = current;
        }

        var gapReport = new JsonObject
        {
            ["assignmentCount"] = assignments.Count,
            ["findingCount"] = findings.Count,
            ["findings"] = findings,
            ["suggestedNextActions"] = new JsonArray(
                "Для генерации новых задач сначала использовать courseStyleProfile как эталон стиля.",
                "Для мест с high severity генерировать bridge tasks перед сложным заданием.",
                "Для слабых code-test задач добавить hidden tests и referenceSolution.",
                "Не менять курс автоматически: сначала показать скрытые черновики/план преподавателю."),
            ["summary"] = findings.Count == 0
                ? "Критичных пробелов по доступному контексту не найдено, но генерацию всё равно нужно сверять со стилем курса."
                : $"Найдено замечаний по курсу: {findings.Count}. Отчёт сохранён в рабочую память агента."
        };

        state.WorkingMemory["courseGapReport"] = gapReport.DeepClone();
        state.Notes.Add($"Learning gap report saved: {findings.Count} finding(s).");
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = gapReport["summary"]?.ToString(),
            ["courseGapReport"] = gapReport.DeepClone()
        };
    }


    public static JsonObject LoadEditableAssignments(AgentLoopState state)
    {
        var assignments = GetAssignments(state);
        var editable = new JsonArray(assignments.Select(x => new JsonObject
        {
            ["index"] = x.Index,
            ["id"] = x.Id,
            ["title"] = x.Title,
            ["type"] = x.Type,
            ["language"] = x.Language,
            ["difficulty"] = x.Difficulty,
            ["rating"] = x.Rating,
            ["tags"] = new JsonArray(x.Tags.Select(t => JsonValue.Create(t)).ToArray<JsonNode?>()),
            ["publicTests"] = x.PublicTestCount,
            ["hiddenTests"] = x.HiddenTestCount,
            ["hasStarterCode"] = x.HasStarterCode,
            ["hasReferenceSolution"] = x.HasReferenceSolution,
            ["descriptionLength"] = x.Description?.Length ?? 0,
            ["descriptionPreview"] = Trim(x.Description, 600),
            ["concepts"] = new JsonArray(ExtractConcepts(x).Distinct(StringComparer.OrdinalIgnoreCase).Select(t => JsonValue.Create(t)).ToArray<JsonNode?>())
        }).ToArray<JsonNode?>());

        var missingIds = assignments.Count(x => string.IsNullOrWhiteSpace(x.Id));
        var payload = new JsonObject
        {
            ["assignmentCount"] = assignments.Count,
            ["editableCount"] = assignments.Count - missingIds,
            ["missingIdCount"] = missingIds,
            ["assignments"] = editable,
            ["summary"] = missingIds == 0
                ? $"Открыто заданий для анализа и правок: {assignments.Count}."
                : $"Открыто заданий: {assignments.Count}, но у {missingIds} нет id — их нельзя безопасно применить как patch."
        };
        state.WorkingMemory["editableAssignments"] = payload.DeepClone();
        state.Notes.Add($"Editable assignments loaded: {assignments.Count}; missing ids: {missingIds}.");
        return new JsonObject
        {
            ["ok"] = assignments.Count > 0,
            ["assignmentCount"] = assignments.Count,
            ["editableCount"] = assignments.Count - missingIds,
            ["summary"] = payload["summary"]?.ToString(),
            ["editableAssignments"] = payload.DeepClone()
        };
    }

    public static JsonObject AnalyzeAssignmentComplexity(AgentLoopState state)
    {
        var assignments = GetAssignments(state);
        var items = new JsonArray();
        foreach (var item in assignments)
        {
            var concepts = ExtractConcepts(item).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var conceptScore = concepts.Sum(ConceptWeight);
            var testCount = item.PublicTestCount + item.HiddenTestCount;
            var textScore = item.Description.Length switch
            {
                < 120 => 0,
                < 360 => 4,
                < 800 => 8,
                _ => 12
            };
            var typeScore = item.Type.Contains("code", StringComparison.OrdinalIgnoreCase) ? 10 : item.Type.Contains("math", StringComparison.OrdinalIgnoreCase) ? 8 : 5;
            var declaredDifficultyScore = (item.Difficulty ?? 1) * 12;
            var totalScore = typeScore + declaredDifficultyScore + conceptScore + textScore + Math.Min(20, testCount * 2) + (item.HasReferenceSolution ? 4 : 0);
            var estimatedDifficulty = totalScore >= 68 ? 3 : totalScore >= 42 ? 2 : 1;
            var suggestedRating = Math.Max(1, RoundToNearest5(item.Index * 10 + (estimatedDifficulty - 1) * 15 + concepts.Count * 3 + Math.Min(10, testCount)));
            var confidence = string.IsNullOrWhiteSpace(item.Description) ? "low" : item.HiddenTestCount == 0 || string.IsNullOrWhiteSpace(item.Id) ? "medium" : "high";

            items.Add(new JsonObject
            {
                ["index"] = item.Index,
                ["assignmentId"] = item.Id,
                ["title"] = item.Title,
                ["type"] = item.Type,
                ["language"] = item.Language,
                ["oldRating"] = item.Rating,
                ["oldDifficulty"] = item.Difficulty,
                ["estimatedDifficulty"] = estimatedDifficulty,
                ["suggestedRating"] = suggestedRating,
                ["score"] = totalScore,
                ["confidence"] = confidence,
                ["concepts"] = new JsonArray(concepts.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>()),
                ["reason"] = BuildComplexityReason(item, concepts, estimatedDifficulty, totalScore)
            });
        }

        var report = new JsonObject
        {
            ["assignmentCount"] = assignments.Count,
            ["items"] = items,
            ["summary"] = $"Оценена сложность заданий: {assignments.Count}. Рейтинг предлагается по порядку курса, заявленной сложности, понятиям, тестам и объёму условия."
        };
        state.WorkingMemory["assignmentComplexityReport"] = report.DeepClone();
        state.Notes.Add($"Assignment complexity analyzed for {assignments.Count} assignments.");
        return new JsonObject
        {
            ["ok"] = assignments.Count > 0,
            ["summary"] = report["summary"]?.ToString(),
            ["assignmentComplexityReport"] = report.DeepClone()
        };
    }

}
