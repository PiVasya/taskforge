using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using taskforge.Data.Models.DTO.AI;
using taskforge.Data.Models.Entities;
using taskforge.Data.Models.Entities.AI;

namespace taskforge.Services.AI;

public sealed partial class AiJobService
{
    public async Task<PublishAiDraftResultDto?> PublishDraftAsync(Guid id, Guid reviewedByUserId, PublishAiDraftRequestDto request, CancellationToken ct = default)
    {
        var draft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (draft == null) return null;

        using var doc = JsonDocument.Parse(draft.DraftJson);
        var root = doc.RootElement;
        var courseId = request.CourseId ?? draft.CourseId ?? ExtractGuid(root, "courseId");
        var selfCheckStatus = ExtractSelfCheckStatus(root);
        var isApproved = string.Equals(draft.Status, "approved", StringComparison.OrdinalIgnoreCase);
        var requiresPassedSelfCheck = _aiOptions.RequirePassedSelfCheckForPublish;
        // If draft was manually approved by admin, treat as force-publish
        var effectiveForce = request.ForceWithoutPassedSelfCheck || isApproved;
        var isFallbackDraft = IsFallbackDraft(root);
        if (isFallbackDraft && !effectiveForce)
            throw new ValidationException("Этот черновик создан fallback-веткой после сбоя/таймаута AI. Сначала перегенерируй или одобри вручную, если публиковать всё-таки нужно.");
        if (requiresPassedSelfCheck && !effectiveForce)
        {
            if (string.IsNullOrWhiteSpace(selfCheckStatus))
                throw new ValidationException("Self-check не пройден. Сначала одобри черновик или запусти self-check.");
            if (!string.Equals(selfCheckStatus, "passed", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException($"Self-check не пройден (статус: {selfCheckStatus}). Одобри черновик вручную или перезапусти self-check.");
        }
        if (courseId == null) throw new ValidationException("Курс не указан. Выбери курс перед публикацией.");

        var courseExists = await _db.Courses.AsNoTracking().AnyAsync(x => x.Id == courseId.Value, ct);
        if (!courseExists) throw new ValidationException("Курс не найден. Проверь, что курс существует.");

        var assignmentType = NormalizeDraftAssignmentType(draft.AssignmentType, root);
        if (assignmentType != "math" && assignmentType != "test" && assignmentType != "code-test")
            throw new ValidationException($"Публикация поддерживается только для math, test, code-test. Текущий тип: {assignmentType}");

        var title = NormalizePublishedDraftTitle(request.TitleOverride ?? ReadString(root, "title") ?? draft.Title ?? string.Empty, ReadString(root, "description"));
        if (string.IsNullOrWhiteSpace(title)) throw new ValidationException("У черновика нет названия (title).");

        var description = NormalizeDraftDescription(root);
        if (string.IsNullOrWhiteSpace(description)) throw new ValidationException("У черновика нет условия (description). AI не сгенерировал описание.");

        var difficulty = Clamp(ReadInt(root, "difficulty") ?? request.Difficulty ?? 2, 1, 3);
        var rating = Math.Max(0, request.Rating ?? ReadInt(root, "rating") ?? 1);
        var tags = request.Tags ?? ReadString(root, "tags");
        var sort = request.Sort ?? await GetNextSortAsync(courseId.Value, ct);

        var assignment = new TaskAssignment
        {
            Id = Guid.NewGuid(),
            CourseId = courseId.Value,
            Title = title,
            Description = description,
            Type = assignmentType,
            Difficulty = difficulty,
            Rating = rating,
            Tags = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim(),
            Sort = sort,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.TaskAssignments.Add(assignment);

        if (assignmentType == "math")
            await PublishMathDraftAsync(assignment.Id, root, ct);
        else if (assignmentType == "test")
            await PublishTestDraftAsync(assignment.Id, root, ct);
        else if (assignmentType == "code-test")
            await PublishCodeDraftAsync(assignment, root, ct);

        draft.Status = "published";
        draft.ReviewedByUserId = reviewedByUserId;
        draft.ReviewedAtUtc = DateTime.UtcNow;
        draft.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return new PublishAiDraftResultDto
        {
            DraftId = draft.Id,
            AssignmentId = assignment.Id,
            CourseId = assignment.CourseId,
            AssignmentType = assignment.Type,
            Title = assignment.Title,
        };
    }

    private async Task<int> GetNextSortAsync(Guid courseId, CancellationToken ct)
    {
        var maxSort = await _db.TaskAssignments.AsNoTracking()
            .Where(x => x.CourseId == courseId)
            .Select(x => (int?)x.Sort)
            .MaxAsync(ct);
        return (maxSort ?? -1) + 1;
    }

    private async Task PublishMathDraftAsync(Guid assignmentId, JsonElement root, CancellationToken ct)
    {
        var settingsNode = GetPropertyOrNull(root, "settings");
        var settings = new TaskMathSettings
        {
            Id = Guid.NewGuid(),
            TaskAssignmentId = assignmentId,
            MaxAttempts = Math.Max(1, ReadInt(settingsNode, "maxAttempts") ?? 3),
            PassPercent = Clamp(ReadInt(settingsNode, "passPercent") ?? 60, 1, 100),
            ShuffleBlocks = ReadBool(settingsNode, "shuffleBlocks") ?? false,
            AllowReview = ReadBool(settingsNode, "allowReview") ?? true,
            AttemptTimeLimitsJson = JsonSerializer.Serialize(ReadNullableIntArray(settingsNode, "attemptTimeLimitsSeconds"), JsonOptions),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.TaskMathSettings.Add(settings);

        var blocksNode = GetPropertyOrNull(root, "blocks");
        var blocks = blocksNode.ValueKind == JsonValueKind.Array ? blocksNode.EnumerateArray().ToList() : new List<JsonElement>();
        if (blocks.Count == 0)
        {
            blocks.Add(JsonDocument.Parse("{\"blockType\":\"info\",\"title\":\"Условие\",\"promptContent\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Заполни блоки вручную: AI не вернул ни одного math-блока.\"}]}]}} ").RootElement.Clone());
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var node = blocks[i];
            var kind = NormalizeMathKind(ReadString(node, "blockType") ?? ReadString(node, "kind") ?? "info");
            var prompt = (ReadString(node, "title") ?? ReadString(node, "prompt") ?? $"Блок {i + 1}").Trim();
            var promptContentJson = ExtractRichPromptContent(node);
            var score = Math.Max(0, ReadInt(node, "points") ?? ReadInt(node, "score") ?? (kind == "info" ? 0 : 1));
            var isRequired = ReadBool(node, "isRequired") ?? true;

            var entity = new TaskMathBlock
            {
                Id = Guid.NewGuid(),
                TaskAssignmentId = assignmentId,
                Order = i,
                Kind = kind,
                Prompt = string.IsNullOrWhiteSpace(prompt) ? $"Блок {i + 1}" : prompt,
                PromptContentJson = promptContentJson,
                Score = score,
                IsRequired = isRequired,
                DataJson = BuildMathBlockDataJson(kind, node),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.TaskMathBlocks.Add(entity);
        }

        await Task.CompletedTask;
    }

    private async Task PublishCodeDraftAsync(TaskAssignment assignment, JsonElement root, CancellationToken ct)
    {
        var langs = ReadStringArray(root, "allowedLanguages");
        assignment.AllowedLanguagesCsv = string.Join(",", langs.Count > 0 ? langs : new List<string> { "python", "cpp", "csharp" });
        assignment.CodeForbiddenCallsJson = BuildStringListDocument(root, "forbiddenCalls");
        assignment.CodeRequiredCallsJson = BuildStringListDocument(root, "requiredCalls");

        var publicTests = ReadTestCases(root, false, "publicTests", "tests").ToList();
        var hiddenTests = ReadTestCases(root, true, "hiddenTests").ToList();
        if (publicTests.Count < 2) throw new ValidationException($"Нужно минимум 2 открытых теста (publicTests), сейчас: {publicTests.Count}.");
        if (hiddenTests.Count < 5) throw new ValidationException($"Нужно минимум 5 скрытых тестов (hiddenTests), сейчас: {hiddenTests.Count}.");
        var allTests = publicTests.Concat(hiddenTests).ToList();

        foreach (var test in allTests)
        {
            test.TaskAssignmentId = assignment.Id;
            _db.TaskTestCases.Add(test);
        }

        await Task.CompletedTask;
    }

    private async Task PublishTestDraftAsync(Guid assignmentId, JsonElement root, CancellationToken ct)
    {
        var settingsNode = GetPropertyOrNull(root, "settings");
        var settings = new TaskTestSettings
        {
            Id = Guid.NewGuid(),
            TaskAssignmentId = assignmentId,
            MaxAttempts = Math.Max(1, ReadInt(settingsNode, "maxAttempts") ?? 3),
            PassPercent = Clamp(ReadInt(settingsNode, "passPercent") ?? 60, 1, 100),
            ShuffleQuestions = ReadBool(settingsNode, "shuffleQuestions") ?? true,
            ShuffleAnswers = ReadBool(settingsNode, "shuffleAnswers") ?? true,
            AllowReview = ReadBool(settingsNode, "allowReview") ?? true,
            AttemptTimeLimitsJson = JsonSerializer.Serialize(ReadNullableIntArray(settingsNode, "attemptTimeLimitsSeconds"), JsonOptions),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.TaskTestSettings.Add(settings);

        var questionsNode = GetPropertyOrNull(root, "questions");
        var questions = questionsNode.ValueKind == JsonValueKind.Array ? questionsNode.EnumerateArray().ToList() : new List<JsonElement>();
        if (questions.Count == 0)
        {
            questions.Add(JsonDocument.Parse("{\"type\":\"text\",\"prompt\":\"AI не вернул ни одного вопроса. Отредактируй тест вручную.\",\"acceptedAnswers\":[\"ok\"],\"trim\":true}").RootElement.Clone());
        }

        for (var i = 0; i < questions.Count; i++)
        {
            var node = questions[i];
            var type = NormalizeTestQuestionType(ReadString(node, "questionType") ?? ReadString(node, "type") ?? "single-choice");
            var prompt = (ReadString(node, "prompt") ?? ReadString(node, "title") ?? $"Вопрос {i + 1}").Trim();
            var entity = new TaskTestQuestion
            {
                Id = Guid.NewGuid(),
                TaskAssignmentId = assignmentId,
                Order = i,
                Type = type,
                Prompt = string.IsNullOrWhiteSpace(prompt) ? $"Вопрос {i + 1}" : prompt,
                DataJson = BuildTestQuestionDataJson(type, node),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.TaskTestQuestions.Add(entity);
        }

        await Task.CompletedTask;
    }

    private static string NormalizeDraftDescription(JsonElement root)
    {
        var raw = (ReadString(root, "description") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // Current storage contract:
        // - legacy assignments may still keep plain text in TaskAssignments.Description
        // - newly published AI drafts must NOT persist raw HTML
        // - rich statements are stored in the same Description column as TipTap JSON
        using var existingDoc = TryParseTipTapDoc(raw);
        if (existingDoc != null)
            return existingDoc.RootElement.GetRawText();

        return LooksLikeHtml(raw)
            ? ConvertHtmlToTipTapJson(raw)
            : ConvertPlainTextToTipTapJson(raw);
    }

    private static JsonDocument? TryParseTipTapDoc(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var typeNode)
                && typeNode.ValueKind == JsonValueKind.String
                && string.Equals(typeNode.GetString(), "doc", StringComparison.OrdinalIgnoreCase))
            {
                return doc;
            }
            doc.Dispose();
        }
        catch
        {
            // ignored
        }
        return null;
    }

    private static bool LooksLikeHtml(string value)
        => !string.IsNullOrWhiteSpace(value) && value.IndexOf('<') >= 0 && value.IndexOf('>') > value.IndexOf('<');

    private static string ConvertHtmlToTipTapJson(string html)
    {
        var normalized = html ?? string.Empty;
        normalized = Regex.Replace(normalized, @"<\s*br\s*/?>", "\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"</\s*p\s*>", "\n\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*p[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"</\s*li\s*>", "\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*li[^>]*>", "• ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"</\s*(ul|ol|div|section|article|h1|h2|h3|h4|h5|h6)\s*>", "\n\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*(ul|ol|div|section|article|h1|h2|h3|h4|h5|h6)[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"</?\s*(strong|b|em|i|u|code|span|pre|blockquote)\b[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<[^>]+>", string.Empty, RegexOptions.IgnoreCase);
        normalized = System.Net.WebUtility.HtmlDecode(normalized);
        return ConvertPlainTextToTipTapJson(normalized);
    }

    private static string ConvertPlainTextToTipTapJson(string raw)
    {
        var text = System.Net.WebUtility.HtmlDecode(raw ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();

        var paragraphs = text
            .Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (paragraphs.Count == 0 && !string.IsNullOrWhiteSpace(text))
            paragraphs.Add(text);

        var content = paragraphs.Select(BuildParagraphNode).ToArray();
        var doc = new { type = "doc", content };
        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    private static object BuildParagraphNode(string paragraph)
    {
        var lines = (paragraph ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var text = string.Join("\n", lines);
        return new
        {
            type = "paragraph",
            content = new[] { new { type = "text", text } }
        };
    }

    private static JsonDocument? BuildStringListDocument(JsonElement root, string property)
    {
        var items = ReadStringArray(root, property);
        if (items.Count == 0) return null;
        return JsonDocument.Parse(JsonSerializer.Serialize(items, JsonOptions));
    }

    private static string NormalizePublishedDraftTitle(string? rawTitle, string? rawDescription)
    {
        var title = (rawTitle ?? string.Empty).Trim();
        var description = System.Net.WebUtility.HtmlDecode(rawDescription ?? string.Empty);

        static string ExtractFallbackTitle(string descriptionText)
        {
            var plain = Regex.Replace(descriptionText ?? string.Empty, "<[^>]+>", " ");
            plain = plain.Replace("\r", " ").Replace("\n", " ");
            plain = Regex.Replace(plain, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(plain)) return "Задание";

            var lowered = plain.ToLowerInvariant();
            if (lowered.Contains("умнож") && lowered.Contains("матриц")) return "Умножение матриц";
            if ((lowered.Contains("гаус") || lowered.Contains("ранг")) && lowered.Contains("матриц")) return "Ранг матрицы";
            if (lowered.Contains("диагон") && lowered.Contains("матриц")) return "Сумма диагонали матрицы";
            if (lowered.Contains("вывед") && lowered.Contains("матриц")) return "Вывод матрицы";
            if (lowered.Contains("сумм") && lowered.Contains("строк") && lowered.Contains("матриц")) return "Суммы строк матрицы";

            var sentence = plain.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (string.IsNullOrWhiteSpace(sentence)) return "Задание";
            sentence = Regex.Replace(sentence, @"^(реализуйте|напишите программу|требуется|постройте задачу на)\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (sentence.Length > 64) sentence = sentence[..64].TrimEnd() + "...";
            return string.IsNullOrWhiteSpace(sentence) ? "Задание" : sentence;
        }

        var looksGeneric = Regex.IsMatch(title, @"^(task|assignment|задание)\s*#?\s*\d+(\.\d+)?$", RegexOptions.IgnoreCase)
            || title == "__PENDING_TITLE__";
        var looksEnglishSlotTitle = Regex.IsMatch(title, @"^[A-Za-z0-9\-\s]+$")
            && title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8;

        if (string.IsNullOrWhiteSpace(title) || looksGeneric || looksEnglishSlotTitle)
            return ExtractFallbackTitle(description);

        return title;
    }

    private static string NormalizeDraftAssignmentType(string? assignmentType, JsonElement root)
    {
        var value = (assignmentType ?? ReadString(root, "assignmentType") ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "quiz" => "test",
            _ => value,
        };
    }

    private static string NormalizeMathKind(string kind)
    {
        var value = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "singlechoice" => "single-choice",
            "multichoice" => "multi-choice",
            _ when value == "single-choice" || value == "multi-choice" || value == "info" || value == "number" || value == "expression" || value == "set" || value == "order" || value == "match" => value,
            _ => "info",
        };
    }

    private static string NormalizeTestQuestionType(string type)
    {
        var value = (type ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "singlechoice" => "single-choice",
            "multichoice" => "multi-choice",
            _ when value == "single-choice" || value == "multi-choice" || value == "fill" || value == "text" => value,
            _ => "single-choice",
        };
    }

    private static string? ExtractRichPromptContent(JsonElement node)
    {
        if (node.TryGetProperty("promptContentJson", out var existingJson) && existingJson.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(existingJson.GetString()) ? null : existingJson.GetString();

        if (node.TryGetProperty("promptContent", out var richNode) && richNode.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return JsonSerializer.Serialize(richNode, JsonOptions);

        return null;
    }

    private static string BuildMathBlockDataJson(string kind, JsonElement node)
    {
        object payload = kind switch
        {
            "single-choice" or "multi-choice" => new
            {
                options = ReadOptions(node, "options"),
                correctOptionKeys = ReadStringArray(node, "correctOptionKeys", "correctKeys", "correct")
            },
            "number" or "expression" or "set" => new
            {
                acceptedAnswers = ReadStringArray(node, "acceptedAnswers", "answers", "correctAnswers"),
                caseSensitive = ReadBool(node, "caseSensitive") ?? false,
                trim = ReadBool(node, "trim") ?? true,
                numericTolerance = kind == "number" ? (ReadDouble(node, "numericTolerance") ?? ReadDouble(node, "tolerance")) : null,
            },
            "order" => new
            {
                items = ReadStringArray(node, "items", "orderItems", "steps")
            },
            "match" => new
            {
                leftItems = ReadOptions(node, "leftItems", "matchLeftItems"),
                rightItems = ReadOptions(node, "rightItems", "matchRightItems"),
                pairs = ReadPairs(node, "pairs", "matchPairs")
            },
            _ => new { }
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string BuildTestQuestionDataJson(string type, JsonElement node)
    {
        object payload = (type == "single-choice" || type == "multi-choice")
            ? new
            {
                options = ReadOptions(node, "options"),
                correctOptionKeys = ReadStringArray(node, "correctOptionKeys", "correctKeys", "correct")
            }
            : new
            {
                acceptedAnswers = ReadStringArray(node, "acceptedAnswers", "answers", "correctAnswers"),
                caseSensitive = ReadBool(node, "caseSensitive") ?? false,
                trim = ReadBool(node, "trim") ?? true,
            };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }


    private static List<TaskTestCase> ReadTestCases(JsonElement node, bool isHidden, params string[] names)
    {
        foreach (var name in names)
        {
            var arr = GetPropertyOrNull(node, name);
            if (arr.ValueKind != JsonValueKind.Array) continue;
            var list = new List<TaskTestCase>();
            foreach (var item in arr.EnumerateArray())
            {
                var input = ReadString(item, "input") ?? string.Empty;
                var output = ReadString(item, "expectedOutput") ?? ReadString(item, "output") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(input) && string.IsNullOrWhiteSpace(output)) continue;
                list.Add(new TaskTestCase
                {
                    Id = Guid.NewGuid(),
                    Input = input,
                    ExpectedOutput = output,
                    IsHidden = isHidden,
                });
            }
            return list;
        }
        return new List<TaskTestCase>();
    }

    private static List<object> ReadOptions(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            var arr = GetPropertyOrNull(node, name);
            if (arr.ValueKind != JsonValueKind.Array) continue;
            var list = new List<object>();
            var idx = 0;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var optionText = item.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(optionText))
                        list.Add(new { key = idx.ToString(), text = optionText });
                    idx++;
                    continue;
                }

                var key = ReadString(item, "key") ?? idx.ToString();
                var text = ReadString(item, "text") ?? ReadString(item, "label") ?? ReadString(item, "title") ?? key;
                if (!string.IsNullOrWhiteSpace(text))
                    list.Add(new { key = key.Trim(), text = text.Trim() });
                idx++;
            }
            return list;
        }
        return new List<object>();
    }

    private static List<object> ReadPairs(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            var arr = GetPropertyOrNull(node, name);
            if (arr.ValueKind != JsonValueKind.Array) continue;
            var list = new List<object>();
            foreach (var item in arr.EnumerateArray())
            {
                var leftKey = ReadString(item, "leftKey") ?? ReadString(item, "left") ?? string.Empty;
                var rightKey = ReadString(item, "rightKey") ?? ReadString(item, "right") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(leftKey) && !string.IsNullOrWhiteSpace(rightKey))
                    list.Add(new { leftKey = leftKey.Trim(), rightKey = rightKey.Trim() });
            }
            return list;
        }
        return new List<object>();
    }

    private static List<string> ReadStringArray(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            var arr = GetPropertyOrNull(node, name);
            if (arr.ValueKind == JsonValueKind.Array)
            {
                return arr.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!.Trim())
                    .ToList();
            }
            if (arr.ValueKind == JsonValueKind.String)
            {
                return arr.GetString()!
                    .Split(new[] { '\n', ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
            }
        }
        return new List<string>();
    }

    private static List<int?> ReadNullableIntArray(JsonElement node, string name)
    {
        var arr = GetPropertyOrNull(node, name);
        if (arr.ValueKind != JsonValueKind.Array) return new List<int?>();
        return arr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n) ? (int?)n : null).ToList();
    }

    private static JsonElement GetPropertyOrNull(JsonElement node, string name)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : default;

    private static string? ReadString(JsonElement node, string property)
    {
        var value = GetPropertyOrNull(node, property);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement node, string property)
    {
        var value = GetPropertyOrNull(node, property);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n)) return n;
        return null;
    }

    private static double? ReadDouble(JsonElement node, string property)
    {
        var value = GetPropertyOrNull(node, property);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out n)) return n;
        return null;
    }

    private static bool? ReadBool(JsonElement node, string property)
    {
        var value = GetPropertyOrNull(node, property);
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b)) return b;
        return null;
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}
