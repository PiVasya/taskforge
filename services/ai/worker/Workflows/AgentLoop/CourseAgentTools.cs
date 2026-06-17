using System.Text.Json.Nodes;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public static class CourseAgentTools
{
    private static readonly string[] ConceptKeywords =
    {
        "ввод", "вывод", "cout", "cin", "printf", "scanf", "переменн", "int", "float", "double", "char",
        "арифмет", "деление", "остат", "услов", "if", "else", "switch", "цикл", "for", "while", "массив",
        "список", "коллекц", "list", "array", "строк", "string", "функц", "метод", "класс", "объект",
        "ооп", "наслед", "интерфейс", "исключ", "файл", "json", "api", "http", "sql", "react", "redux",
        "html", "css", "тест", "math", "формат", "парс", "regex", "linq"
    };

    private sealed record AssignmentSnapshot(
        int Index,
        string Source,
        string? Id,
        string Title,
        string Type,
        string? Language,
        int? Difficulty,
        int? Rating,
        List<string> Tags,
        string Description,
        int PublicTestCount,
        int HiddenTestCount,
        bool HasStarterCode,
        bool HasReferenceSolution,
        JsonObject Raw);

    public static JsonObject MapCourseStructure(AgentLoopState state)
    {
        var assignments = GetAssignments(state);
        var byType = assignments.GroupBy(x => Normalize(x.Type)).ToDictionary(g => g.Key, g => g.Count());
        var byLanguage = assignments.Where(x => !string.IsNullOrWhiteSpace(x.Language)).GroupBy(x => Normalize(x.Language!)).ToDictionary(g => g.Key, g => g.Count());
        var concepts = BuildConceptTimeline(assignments);
        var difficultyValues = assignments.Where(x => x.Difficulty.HasValue).Select(x => x.Difficulty!.Value).ToList();
        var ratingValues = assignments.Where(x => x.Rating.HasValue).Select(x => x.Rating!.Value).ToList();

        var map = new JsonObject
        {
            ["assignmentCount"] = assignments.Count,
            ["sources"] = ToCountObject(assignments.GroupBy(x => x.Source).ToDictionary(g => g.Key, g => g.Count())),
            ["types"] = ToCountObject(byType),
            ["languages"] = ToCountObject(byLanguage),
            ["difficulty"] = BuildNumberStats(difficultyValues),
            ["rating"] = BuildNumberStats(ratingValues),
            ["firstAssignments"] = new JsonArray(assignments.Take(8).Select(ToSnapshotJson).ToArray<JsonNode?>()),
            ["lastAssignments"] = new JsonArray(assignments.TakeLast(8).Select(ToSnapshotJson).ToArray<JsonNode?>()),
            ["conceptTimeline"] = concepts,
            ["courseShape"] = BuildCourseShape(assignments),
            ["summary"] = BuildCourseMapSummary(assignments, byType, concepts.Count)
        };

        state.WorkingMemory["courseMap"] = map.DeepClone();
        state.Notes.Add($"Course structure mapped: {assignments.Count} assignment snapshots, {concepts.Count} concept entries.");
        return new JsonObject
        {
            ["ok"] = true,
            ["assignmentCount"] = assignments.Count,
            ["summary"] = map["summary"]?.ToString(),
            ["courseMap"] = map.DeepClone()
        };
    }

    public static JsonObject ExtractCourseStyle(AgentLoopState state)
    {
        var assignments = GetAssignments(state);
        var titleNumbered = assignments.Count(x => StartsWithNumber(x.Title));
        var hasGoal = assignments.Count(x => ContainsAny(x.Description, "Цель задания", "Цель:", "Цель"));
        var hasSteps = assignments.Count(x => ContainsAny(x.Description, "Пошагово", "Следуй шагам", "Шаг"));
        var hasInputFormat = assignments.Count(x => ContainsAny(x.Description, "Формат ввода", "Ввод:"));
        var hasOutputFormat = assignments.Count(x => ContainsAny(x.Description, "Формат вывода", "Вывод:"));
        var hasBackticks = assignments.Count(x => x.Description.Contains('`'));
        var shortDescriptions = assignments.Count(x => x.Description.Trim().Length is > 0 and < 160);
        var publicCounts = assignments.Select(x => x.PublicTestCount).Where(x => x > 0).ToList();
        var hiddenCounts = assignments.Select(x => x.HiddenTestCount).Where(x => x > 0).ToList();
        var commonTags = assignments.SelectMany(x => x.Tags).Where(x => !string.IsNullOrWhiteSpace(x)).GroupBy(Normalize).OrderByDescending(g => g.Count()).Take(20).ToDictionary(g => g.Key, g => g.Count());

        var style = new JsonObject
        {
            ["assignmentCount"] = assignments.Count,
            ["titleStyle"] = new JsonObject
            {
                ["numberedTitles"] = titleNumbered,
                ["numberedRatio"] = Ratio(titleNumbered, assignments.Count),
                ["examples"] = new JsonArray(assignments.Take(8).Select(x => JsonValue.Create(x.Title)).ToArray<JsonNode?>())
            },
            ["descriptionStyle"] = new JsonObject
            {
                ["usesGoalBlocks"] = hasGoal,
                ["usesStepByStepBlocks"] = hasSteps,
                ["usesInputFormatSections"] = hasInputFormat,
                ["usesOutputFormatSections"] = hasOutputFormat,
                ["usesInlineCodeBackticks"] = hasBackticks,
                ["shortDescriptions"] = shortDescriptions,
                ["studentFacingRecommendation"] = BuildDescriptionStyleRecommendation(assignments.Count, hasGoal, hasSteps, hasInputFormat, hasBackticks)
            },
            ["testingStyle"] = new JsonObject
            {
                ["averagePublicTests"] = Average(publicCounts),
                ["averageHiddenTests"] = Average(hiddenCounts),
                ["assignmentsWithoutTests"] = assignments.Count(x => x.PublicTestCount + x.HiddenTestCount == 0),
                ["assignmentsWithoutHiddenTests"] = assignments.Count(x => x.PublicTestCount + x.HiddenTestCount > 0 && x.HiddenTestCount == 0)
            },
            ["commonTags"] = ToCountObject(commonTags),
            ["languageProfile"] = ToCountObject(assignments.Where(x => !string.IsNullOrWhiteSpace(x.Language)).GroupBy(x => Normalize(x.Language!)).ToDictionary(g => g.Key, g => g.Count())),
            ["summary"] = "Стиль курса извлечён из существующих заданий: названия, описания, тесты, теги и язык. Используй это как эталон при генерации новых задач."
        };

        state.WorkingMemory["courseStyleProfile"] = style.DeepClone();
        state.Notes.Add("Course style profile extracted and saved to working memory.");
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = "Стиль существующих заданий сохранён в памяти агента.",
            ["courseStyleProfile"] = style.DeepClone()
        };
    }

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

    public static JsonObject ProposeAssignmentPatchSet(AgentLoopState state, JsonObject args, int maxOperations)
    {
        var operation = Normalize(args["operation"]?.ToString() ?? "rerate_assignments");
        var field = Normalize(args["field"]?.ToString() ?? "rating");
        if (operation.Contains("rating")) field = "rating";
        if (field is not "rating" and not "difficulty" and not "title" and not "description" and not "tags" and not "language" and not "type" and not "isvisible")
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["summary"] = $"Поле '{field}' пока нельзя безопасно редактировать patch set-ом."
            };
        }

        if (state.WorkingMemory["assignmentComplexityReport"] is not JsonObject complexity)
        {
            AnalyzeAssignmentComplexity(state);
            complexity = state.WorkingMemory["assignmentComplexityReport"] as JsonObject ?? new JsonObject();
        }

        var courseId = state.Job.CourseId?.ToString() ?? state.WorkingMemory["courseDigest"]?["selectedCourseId"]?.ToString();
        var items = complexity["items"] as JsonArray ?? new JsonArray();
        var patches = new JsonArray();
        foreach (var node in items.OfType<JsonObject>())
        {
            if (patches.Count >= maxOperations) break;
            var assignmentId = node["assignmentId"]?.ToString();
            if (string.IsNullOrWhiteSpace(assignmentId)) continue;
            var title = node["title"]?.ToString() ?? "Задание";
            var oldRating = GetIntNode(node["oldRating"]);
            var newRating = GetIntNode(node["suggestedRating"]);
            if (field == "rating")
            {
                if (!newRating.HasValue) continue;
                if (oldRating.HasValue && oldRating.Value == newRating.Value) continue;
                patches.Add(BuildAssignmentPatch(
                    courseId,
                    assignmentId,
                    title,
                    field,
                    oldRating.HasValue ? JsonValue.Create(oldRating.Value) : null,
                    JsonValue.Create(newRating.Value),
                    node["reason"]?.ToString() ?? "Рейтинг рассчитан по сложности задания и месту в курсе.",
                    node));
            }
            else if (field == "difficulty")
            {
                var oldDifficulty = GetIntNode(node["oldDifficulty"]);
                var newDifficulty = GetIntNode(node["estimatedDifficulty"]);
                if (!newDifficulty.HasValue) continue;
                if (oldDifficulty.HasValue && oldDifficulty.Value == newDifficulty.Value) continue;
                patches.Add(BuildAssignmentPatch(
                    courseId,
                    assignmentId,
                    title,
                    field,
                    oldDifficulty.HasValue ? JsonValue.Create(oldDifficulty.Value) : null,
                    JsonValue.Create(newDifficulty.Value),
                    node["reason"]?.ToString() ?? "Сложность рассчитана по типу задания, понятиям и тестам.",
                    node));
            }
        }

        var patchSet = new JsonObject
        {
            ["type"] = "course_patch_set",
            ["title"] = field == "rating" ? "Патч курса: перерасчёт рейтингов заданий" : "Патч курса: массовое редактирование заданий",
            ["operation"] = operation,
            ["field"] = field,
            ["courseId"] = courseId,
            ["patchCount"] = patches.Count,
            ["patches"] = patches,
            ["stats"] = new JsonObject
            {
                ["sourceAssignments"] = items.Count,
                ["patches"] = patches.Count,
                ["skippedWithoutId"] = items.OfType<JsonObject>().Count(x => string.IsNullOrWhiteSpace(x["assignmentId"]?.ToString())),
                ["limit"] = maxOperations
            },
            ["summary"] = patches.Count == 0
                ? "Патчи не созданы: по доступным данным нечего менять или у заданий нет id."
                : $"Подготовлено изменений: {patches.Count}. Открой меню патчей, проверь диффы и только затем применяй.",
            ["assistantMessage"] = patches.Count == 0
                ? "Я изучил задания курса, но не нашёл безопасных изменений для patch set. Проверь логи: возможно, у заданий нет id или текущие значения уже совпадают с расчётом."
                : $"Я подготовил patch set на {patches.Count} изменений. Открой меню патчей: там будут диффы как в GitHub, причины и безопасная проверка перед применением."
        };

        state.WorkingMemory["pendingPatchSet"] = patchSet.DeepClone();
        state.FinalMessage = patchSet["assistantMessage"]?.ToString();
        state.ScenarioId = "course_patch_set";
        state.Notes.Add($"Patch set prepared: {patches.Count} patch(es), field={field}.");
        return new JsonObject
        {
            ["ok"] = patches.Count > 0,
            ["summary"] = patchSet["summary"]?.ToString(),
            ["patchSet"] = patchSet.DeepClone()
        };
    }

    public static JsonObject ReviewPatchSet(AgentLoopState state)
    {
        if (state.WorkingMemory["pendingPatchSet"] is not JsonObject patchSet)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["summary"] = "Нет подготовленного patch set для проверки."
            };
        }

        var patches = patchSet["patches"] as JsonArray ?? new JsonArray();
        var issues = new JsonArray();
        foreach (var patch in patches.OfType<JsonObject>())
        {
            if (string.IsNullOrWhiteSpace(patch["assignmentId"]?.ToString())) issues.Add("Патч без assignmentId.");
            if (patch["changes"] is not JsonArray changes || changes.Count == 0) issues.Add($"{patch["title"]}: нет изменений.");
            else
            {
                foreach (var change in changes.OfType<JsonObject>())
                {
                    var field = Normalize(change["field"]?.ToString() ?? string.Empty);
                    if (field is not "rating" and not "difficulty" and not "title" and not "description" and not "tags" and not "language" and not "type" and not "isvisible")
                        issues.Add($"{patch["title"]}: запрещённое поле {field}.");
                }
            }
        }

        var review = new JsonObject
        {
            ["patchCount"] = patches.Count,
            ["issueCount"] = issues.Count,
            ["issues"] = issues,
            ["isGoodEnoughToShow"] = patches.Count > 0 && issues.Count == 0,
            ["summary"] = issues.Count == 0
                ? $"Patch set проверен: {patches.Count} изменений готовы к просмотру в меню патчей."
                : $"Patch set содержит проблемы: {issues.Count}."
        };
        state.WorkingMemory["patchSetReview"] = review.DeepClone();
        state.Notes.Add("Patch set reviewed before finish.");
        return new JsonObject
        {
            ["ok"] = issues.Count == 0 && patches.Count > 0,
            ["summary"] = review["summary"]?.ToString(),
            ["patchSetReview"] = review.DeepClone()
        };
    }

    public static JsonObject PlanCourseEnrichment(AgentLoopState state)
    {
        var intent = state.WorkingMemory["intent"]?.DeepClone();
        var courseMap = state.WorkingMemory["courseMap"]?.DeepClone();
        var style = state.WorkingMemory["courseStyleProfile"]?.DeepClone();
        var gaps = state.WorkingMemory["courseGapReport"]?.DeepClone();
        var assignments = GetAssignments(state);
        var userText = state.Job.UserText;

        var brief = new JsonObject
        {
            ["userRequest"] = userText,
            ["intent"] = intent,
            ["courseMap"] = courseMap,
            ["courseStyleProfile"] = style,
            ["courseGapReport"] = gaps,
            ["generationRules"] = new JsonArray(
                "Сохраняй стиль существующего курса: похожие названия, уровень подробности, формат описаний, язык и теги.",
                "Не добавляй админский или служебный текст в видимое описание задания.",
                "Если задача обучающая, объясняй один маленький новый навык и давай пошаговые действия.",
                "Если задача проверочная, формулируй кратко и без подсказок.",
                "starterCode не должен содержать готовое решение.",
                "Для code-test нужны рабочий referenceSolution, public tests и hidden tests.",
                "Если курс уже имеет близкий стиль, новые задачи должны выглядеть как естественное продолжение, а не как отдельный AI-блок."),
            ["recommendedContextAnchors"] = new JsonArray(assignments.TakeLast(6).Select(ToSnapshotJson).ToArray<JsonNode?>()),
            ["nextBestWorkflow"] = ResolveNextWorkflow(state),
            ["summary"] = "Подготовлен единый brief для следующего workflow: запрос, память чата, карта курса, стиль, пробелы и правила качества."
        };

        state.WorkingMemory["courseEnrichmentBrief"] = brief.DeepClone();
        state.Notes.Add("Course enrichment brief prepared for downstream workflow.");
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = brief["summary"]?.ToString(),
            ["nextBestWorkflow"] = brief["nextBestWorkflow"]?.ToString(),
            ["courseEnrichmentBrief"] = brief.DeepClone()
        };
    }

    public static JsonObject SearchCourse(AgentLoopState state, JsonObject args)
    {
        var query = args["query"]?.ToString() ?? state.Job.UserText;
        var concepts = ExtractConcepts(query).ToList();
        var q = Normalize(query);
        var assignments = GetAssignments(state);
        var matches = assignments
            .Select(x => new { Assignment = x, Score = SearchScore(x, q, concepts) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Assignment.Index)
            .Take(20)
            .Select(x => ToSnapshotJson(x.Assignment, x.Score))
            .ToArray<JsonNode?>();

        var result = new JsonObject
        {
            ["query"] = query,
            ["concepts"] = new JsonArray(concepts.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>()),
            ["matchCount"] = matches.Length,
            ["matches"] = new JsonArray(matches),
            ["summary"] = matches.Length == 0
                ? "По доступному контексту курса совпадений не найдено."
                : $"Найдено совпадений в курсе: {matches.Length}."
        };
        state.WorkingMemory["lastCourseSearch"] = result.DeepClone();
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = result["summary"]?.ToString(),
            ["search"] = result.DeepClone()
        };
    }

    public static JsonObject ReviewDelegatedResult(AgentLoopState state)
    {
        if (state.DelegatedResult is null)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["summary"] = "Пока нет результата workflow, который можно проверить."
            };
        }

        var issues = new JsonArray();
        var artifacts = state.DelegatedResult.Artifacts;
        if (artifacts.Count == 0)
            issues.Add("Workflow не вернул материалы/artifacts. Для генерации задач это подозрительно.");

        foreach (var artifact in artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Title))
                issues.Add($"Материал типа {artifact.Type} без названия.");
            if (artifact.Data is JsonObject obj)
            {
                var assignmentType = obj["assignmentType"]?.ToString() ?? obj["type"]?.ToString();
                if (string.Equals(assignmentType, "code-test", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(obj["referenceSolution"]?.ToString()))
                        issues.Add($"{artifact.Title}: нет referenceSolution.");
                    var publicCount = CountArray(obj["publicTests"]) + CountArray(obj["testCases"]);
                    var hiddenCount = CountArray(obj["hiddenTests"]);
                    if (publicCount == 0)
                        issues.Add($"{artifact.Title}: нет public tests.");
                    if (hiddenCount == 0)
                        issues.Add($"{artifact.Title}: нет hidden tests.");
                }
            }
        }

        var review = new JsonObject
        {
            ["artifactCount"] = artifacts.Count,
            ["artifactTypes"] = new JsonArray(artifacts.Select(x => JsonValue.Create(x.Type)).ToArray<JsonNode?>()),
            ["issues"] = issues,
            ["isGoodEnoughToFinish"] = issues.Count == 0,
            ["summary"] = issues.Count == 0
                ? "Результат workflow выглядит пригодным для завершения run-а."
                : $"У результата workflow есть замечания: {issues.Count}."
        };
        state.WorkingMemory["delegatedResultReview"] = review.DeepClone();
        state.Notes.Add("Delegated workflow result reviewed before finish.");
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = review["summary"]?.ToString(),
            ["review"] = review.DeepClone()
        };
    }

    private static string ResolveNextWorkflow(AgentLoopState state)
    {
        var scenario = state.WorkingMemory["intent"]?["scenarioId"]?.ToString() ?? state.ScenarioId;
        return scenario switch
        {
            "course_edit" => "delegate_course_edit",
            "course_analysis" or "course_gap_audit" => "delegate_course_audit",
            "polish_assignment_draft" or "draft_revision" => "delegate_polish_assignment",
            _ => LooksLikeDraftRequest(state.Job.UserText) ? "delegate_assignment_draft" : "answer_directly"
        };
    }

    private static JsonObject BuildCourseShape(List<AssignmentSnapshot> assignments)
    {
        var windows = new JsonArray();
        const int size = 8;
        for (var i = 0; i < assignments.Count; i += size)
        {
            var chunk = assignments.Skip(i).Take(size).ToList();
            if (chunk.Count == 0) continue;
            windows.Add(new JsonObject
            {
                ["fromIndex"] = chunk.First().Index,
                ["toIndex"] = chunk.Last().Index,
                ["count"] = chunk.Count,
                ["types"] = ToCountObject(chunk.GroupBy(x => Normalize(x.Type)).ToDictionary(g => g.Key, g => g.Count())),
                ["concepts"] = new JsonArray(chunk.SelectMany(ExtractConcepts).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).Select(x => JsonValue.Create(x)).ToArray<JsonNode?>())
            });
        }
        return new JsonObject
        {
            ["windowSize"] = size,
            ["windows"] = windows
        };
    }

    private static JsonArray BuildConceptTimeline(List<AssignmentSnapshot> assignments)
    {
        var timeline = new JsonArray();
        foreach (var item in assignments)
        {
            var concepts = ExtractConcepts(item).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
            if (concepts.Count == 0) continue;
            timeline.Add(new JsonObject
            {
                ["index"] = item.Index,
                ["title"] = item.Title,
                ["concepts"] = new JsonArray(concepts.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>())
            });
        }
        return timeline;
    }

    private static string BuildCourseMapSummary(List<AssignmentSnapshot> assignments, Dictionary<string, int> byType, int conceptEntries)
    {
        if (assignments.Count == 0)
            return "В payload не найдено явных заданий курса. Агенту нужно попросить пользователя открыть курс или приложить контекст.";
        var typeText = byType.Count == 0 ? "тип не определён" : string.Join(", ", byType.Select(x => $"{x.Key}: {x.Value}"));
        return $"Карта курса построена: {assignments.Count} заданий, типы: {typeText}, concept entries: {conceptEntries}.";
    }

    private static string BuildDescriptionStyleRecommendation(int total, int hasGoal, int hasSteps, int hasInputFormat, int hasBackticks)
    {
        if (total == 0) return "Нет примеров для извлечения стиля.";
        if (hasGoal > total / 3 || hasSteps > total / 3)
            return "Курс похож на обучающий: новые задания лучше писать с целью, разбором и маленькими шагами.";
        if (hasInputFormat > total / 3)
            return "Курс похож на задачник: новые задания должны иметь аккуратные форматы ввода/вывода и тесты.";
        if (hasBackticks > total / 3)
            return "В курсе часто используется inline-code: сохраняй backticks для кода и терминов.";
        return "Стиль смешанный: сохраняй student-facing формулировки и не добавляй служебную metadata в описание.";
    }

    private static JsonObject BuildNumberStats(List<int> values)
    {
        if (values.Count == 0)
            return new JsonObject { ["available"] = false };
        return new JsonObject
        {
            ["available"] = true,
            ["min"] = values.Min(),
            ["max"] = values.Max(),
            ["average"] = Math.Round(values.Average(), 2)
        };
    }

    private static JsonObject Finding(string severity, string title, string assignmentTitle, string evidence, string recommendation) => new()
    {
        ["severity"] = severity,
        ["title"] = title,
        ["assignmentTitle"] = assignmentTitle,
        ["evidence"] = evidence,
        ["recommendation"] = recommendation
    };


    private static JsonObject BuildAssignmentPatch(string? courseId, string assignmentId, string title, string field, JsonNode? oldValue, JsonNode? newValue, string reason, JsonObject sourceAnalysis)
    {
        var fileName = $"assignments/{SafeFilePart(title)}.json";
        var change = new JsonObject
        {
            ["field"] = field,
            ["oldValue"] = oldValue?.DeepClone(),
            ["newValue"] = newValue?.DeepClone(),
            ["reason"] = reason
        };
        var patch = new JsonObject
        {
            ["operation"] = "update_assignment",
            ["courseId"] = courseId,
            ["assignmentId"] = assignmentId,
            ["title"] = title,
            ["changeCount"] = 1,
            ["changes"] = new JsonArray(change),
            ["analysis"] = sourceAnalysis.DeepClone(),
            ["diff"] = new JsonObject
            {
                ["filePath"] = fileName,
                ["oldPath"] = $"a/{fileName}",
                ["newPath"] = $"b/{fileName}",
                ["hunks"] = new JsonArray(new JsonObject
                {
                    ["header"] = $"@@ assignment.{field} @@",
                    ["lines"] = new JsonArray(
                        new JsonObject { ["type"] = "context", ["text"] = $"// {title}" },
                        new JsonObject { ["type"] = "removed", ["text"] = $"\"{field}\": {JsonLiteral(oldValue)}" },
                        new JsonObject { ["type"] = "added", ["text"] = $"\"{field}\": {JsonLiteral(newValue)}" })
                })
            }
        };
        return patch;
    }

    private static string BuildComplexityReason(AssignmentSnapshot item, List<string> concepts, int estimatedDifficulty, int score)
    {
        var bits = new List<string>
        {
            $"оценка сложности: {estimatedDifficulty}",
            $"score={score}",
            $"позиция в курсе: {item.Index}"
        };
        if (item.Difficulty.HasValue) bits.Add($"текущая difficulty={item.Difficulty.Value}");
        if (concepts.Count > 0) bits.Add("понятия: " + string.Join(", ", concepts.Take(6)));
        if (item.PublicTestCount + item.HiddenTestCount > 0) bits.Add($"тестов: {item.PublicTestCount + item.HiddenTestCount}, hidden: {item.HiddenTestCount}");
        if (!item.HasReferenceSolution && item.Type.Contains("code", StringComparison.OrdinalIgnoreCase)) bits.Add("нет эталонного решения — уверенность ниже");
        return string.Join("; ", bits);
    }

    private static int ConceptWeight(string concept)
    {
        var c = Normalize(concept);
        if (c.Contains("ооп") || c.Contains("класс") || c.Contains("api") || c.Contains("sql") || c.Contains("react")) return 18;
        if (c.Contains("массив") || c.Contains("строк") || c.Contains("функц") || c.Contains("цикл")) return 12;
        if (c.Contains("услов") || c.Contains("if") || c.Contains("scanf") || c.Contains("printf")) return 8;
        return 4;
    }

    private static int RoundToNearest5(int value) => (int)(Math.Round(value / 5.0) * 5);

    private static int? GetIntNode(JsonNode? node)
    {
        if (node is null) return null;
        if (int.TryParse(node.ToString(), out var value)) return value;
        if (double.TryParse(node.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var number)) return (int)Math.Round(number);
        return null;
    }

    private static string SafeFilePart(string value)
    {
        var text = string.Join('-', (value ?? "assignment").Trim().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(text) ? "assignment" : text.Length <= 80 ? text : text[..80];
    }

    private static string JsonLiteral(JsonNode? node)
    {
        if (node is null) return "null";
        var text = node.ToJsonString();
        return string.IsNullOrWhiteSpace(text) ? "null" : text;
    }

    private static JsonObject ToCountObject(Dictionary<string, int> data)
    {
        var obj = new JsonObject();
        foreach (var pair in data.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
            obj[pair.Key] = pair.Value;
        return obj;
    }

    private static JsonObject ToSnapshotJson(AssignmentSnapshot item, int? score = null)
    {
        var obj = new JsonObject
        {
            ["index"] = item.Index,
            ["source"] = item.Source,
            ["id"] = item.Id,
            ["title"] = item.Title,
            ["type"] = item.Type,
            ["language"] = item.Language,
            ["difficulty"] = item.Difficulty,
            ["rating"] = item.Rating,
            ["tags"] = new JsonArray(item.Tags.Take(12).Select(x => JsonValue.Create(x)).ToArray<JsonNode?>()),
            ["concepts"] = new JsonArray(ExtractConcepts(item).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).Select(x => JsonValue.Create(x)).ToArray<JsonNode?>()),
            ["publicTests"] = item.PublicTestCount,
            ["hiddenTests"] = item.HiddenTestCount,
            ["descriptionPreview"] = Trim(item.Description, 360)
        };
        if (score.HasValue) obj["score"] = score.Value;
        return obj;
    }

    private static List<AssignmentSnapshot> GetAssignments(AgentLoopState state)
    {
        var result = new List<AssignmentSnapshot>();
        AddFromMemory(state.WorkingMemory["assignments"], "assignments", result);
        AddFromMemory(state.WorkingMemory["courseOutline"], "courseOutline", result);
        AddFromMemory(state.WorkingMemory["targetAssignments"], "targetAssignments", result);
        AddFromMemory(state.WorkingMemory["focusAssignments"], "focusAssignments", result);
        if (result.Count == 0)
            AddFromMemory(state.WorkingMemory["contextPrompt"], "contextPrompt", result);

        return result
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.Id) ? x.Id! : $"{Normalize(x.Title)}:{x.Index}:{x.Source}")
            .Select(g => g.First())
            .Select((x, index) => x with { Index = index + 1 })
            .ToList();
    }

    private static void AddFromMemory(JsonNode? node, string source, List<AssignmentSnapshot> result)
    {
        switch (node)
        {
            case JsonObject obj:
                CollectAssignments(obj, source, result, 0);
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    AddFromMemory(item, source, result);
                break;
            case JsonValue value:
                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text) && (text.Contains("title", StringComparison.OrdinalIgnoreCase) || text.Contains("зад", StringComparison.OrdinalIgnoreCase)))
                {
                    // Text-only context is intentionally not parsed deeply. It is still kept in AgentState for the LLM.
                }
                break;
        }
    }

    private static void CollectAssignments(JsonObject obj, string source, List<AssignmentSnapshot> result, int depth)
    {
        if (result.Count >= 500 || depth > 10) return;

        if (LooksLikeAssignment(obj))
        {
            result.Add(new AssignmentSnapshot(
                result.Count + 1,
                source,
                GetString(obj, "id", "assignmentId"),
                GetString(obj, "title", "name") ?? "Без названия",
                GetString(obj, "assignmentType", "type") ?? "assignment",
                GetString(obj, "language", "defaultLanguage"),
                GetInt(obj, "difficulty", "level"),
                GetInt(obj, "rating", "order", "sortOrder"),
                GetTags(obj),
                GetString(obj, "description", "descriptionPreview", "body", "condition", "text") ?? string.Empty,
                CountArray(obj["publicTests"]) + CountOpenTestCases(obj["testCases"]),
                CountArray(obj["hiddenTests"]) + CountHiddenTestCases(obj["testCases"]),
                !string.IsNullOrWhiteSpace(GetString(obj, "starterCode", "templateCode")),
                !string.IsNullOrWhiteSpace(GetString(obj, "referenceSolution", "solution", "answer")),
                obj));
        }

        foreach (var child in obj)
        {
            if (child.Value is JsonObject childObj)
                CollectAssignments(childObj, source, result, depth + 1);
            else if (child.Value is JsonArray childArr)
            {
                foreach (var item in childArr)
                    if (item is JsonObject itemObj)
                        CollectAssignments(itemObj, source, result, depth + 1);
            }
        }
    }

    private static bool LooksLikeAssignment(JsonObject obj)
    {
        var title = GetString(obj, "title", "name");
        if (string.IsNullOrWhiteSpace(title)) return false;
        var hasAssignmentField = obj.ContainsKey("description")
                                 || obj.ContainsKey("assignmentType")
                                 || obj.ContainsKey("type")
                                 || obj.ContainsKey("testCases")
                                 || obj.ContainsKey("publicTests")
                                 || obj.ContainsKey("hiddenTests")
                                 || obj.ContainsKey("starterCode")
                                 || obj.ContainsKey("referenceSolution")
                                 || obj.ContainsKey("difficulty")
                                 || obj.ContainsKey("rating");
        var hasCourseOnlyFields = obj.ContainsKey("assignments") && !obj.ContainsKey("description") && !obj.ContainsKey("assignmentType");
        return hasAssignmentField && !hasCourseOnlyFields;
    }

    private static int SearchScore(AssignmentSnapshot item, string query, List<string> concepts)
    {
        var score = 0;
        var haystack = Normalize(string.Join(" ", item.Title, item.Description, item.Type, item.Language, string.Join(" ", item.Tags)));
        if (!string.IsNullOrWhiteSpace(query) && haystack.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 10;
        foreach (var concept in concepts)
            if (haystack.Contains(Normalize(concept), StringComparison.OrdinalIgnoreCase)) score += 3;
        return score;
    }

    private static IEnumerable<string> ExtractConcepts(AssignmentSnapshot item)
        => ExtractConcepts(string.Join(" ", item.Title, item.Description, item.Type, item.Language, string.Join(" ", item.Tags)));

    private static IEnumerable<string> ExtractConcepts(string text)
    {
        var lower = Normalize(text);
        foreach (var keyword in ConceptKeywords)
        {
            if (lower.Contains(Normalize(keyword), StringComparison.OrdinalIgnoreCase))
                yield return keyword;
        }
    }

    private static string? GetString(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node is null) continue;
            if (node is JsonValue) return node.ToString();
            if (node is JsonArray arr) return string.Join(", ", arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        return null;
    }

    private static int? GetInt(JsonObject obj, params string[] keys)
    {
        var raw = GetString(obj, keys);
        if (int.TryParse(raw, out var value)) return value;
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var number)) return (int)Math.Round(number);
        return null;
    }

    private static List<string> GetTags(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("tags", out var node) || node is null)
            return new List<string>();
        if (node is JsonArray arr)
            return arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return node.ToString().Split(new[] { ',', ';', '|', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int CountOpenTestCases(JsonNode? node)
    {
        if (node is not JsonArray arr) return 0;
        return arr.Count(x => x is JsonObject obj && !ReadBool(obj["isHidden"]));
    }

    private static int CountHiddenTestCases(JsonNode? node)
    {
        if (node is not JsonArray arr) return 0;
        return arr.Count(x => x is JsonObject obj && ReadBool(obj["isHidden"]));
    }

    private static int CountArray(JsonNode? node) => node is JsonArray arr ? arr.Count : 0;

    private static bool ReadBool(JsonNode? node)
    {
        if (node is null) return false;
        return bool.TryParse(node.ToString(), out var value) && value;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool StartsWithNumber(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.Length > 0 && char.IsDigit(trimmed[0]);
    }

    private static double Ratio(int part, int total) => total <= 0 ? 0 : Math.Round((double)part / total, 3);
    private static double Average(List<int> values) => values.Count == 0 ? 0 : Math.Round(values.Average(), 2);
    private static string Normalize(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static string Trim(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private static bool LooksLikeDraftRequest(string text)
    {
        var t = Normalize(text);
        return (t.Contains("создай") || t.Contains("сгенер") || t.Contains("придум") || t.Contains("сделай") || t.Contains("накидай"))
               && (t.Contains("задач") || t.Contains("задани") || t.Contains("курс"));
    }
}
