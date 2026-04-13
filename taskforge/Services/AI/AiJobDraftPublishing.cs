using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.DTO.AI;
using taskforge.Data.Models.DTO.TaskMaths;
using taskforge.Data.Models.DTO.TaskTests;
using taskforge.Data.Models.Entities;
using taskforge.Data.Models.Entities.AI;

namespace taskforge.Services.AI;

public sealed partial class AiJobService
{
    private const string NoInputSentinel = "пусто";
    public async Task<PublishAiDraftResultDto?> PublishDraftAsync(Guid id, Guid reviewedByUserId, PublishAiDraftRequestDto request, CancellationToken ct = default)
    {
        var draft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (draft == null) return null;

        using var doc = JsonDocument.Parse(draft.DraftJson);
        var root = doc.RootElement;
        var draftRoot = ExtractPublishableDraftRoot(root);
        var courseId = request.CourseId ?? draft.CourseId ?? ExtractGuid(draftRoot, "courseId");
        var selfCheckStatus = ExtractSelfCheckStatus(draftRoot);
        var isApproved = string.Equals(draft.Status, "approved", StringComparison.OrdinalIgnoreCase);
        var requiresPassedSelfCheck = _aiOptions.RequirePassedSelfCheckForPublish;
        var effectiveForce = request.ForceWithoutPassedSelfCheck || isApproved;
        var isFallbackDraft = IsFallbackDraft(draftRoot);
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

        var course = await _db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == courseId.Value, ct);
        if (course == null) throw new ValidationException("Курс не найден. Проверь, что курс существует.");

        var assignmentType = NormalizeDraftAssignmentType(draft.AssignmentType, draftRoot);
        if (assignmentType != "math" && assignmentType != "test" && assignmentType != "code-test")
            throw new ValidationException($"Публикация поддерживается только для math, test, code-test. Текущий тип: {assignmentType}");

        var title = NormalizePublishedDraftTitle(request.TitleOverride ?? ReadString(draftRoot, "title") ?? draft.Title ?? string.Empty, ReadString(draftRoot, "description"));
        if (string.IsNullOrWhiteSpace(title)) throw new ValidationException("У черновика нет названия (title).");

        var description = NormalizeDraftDescription(draftRoot);
        if (string.IsNullOrWhiteSpace(description)) throw new ValidationException("У черновика нет условия (description). AI не сгенерировал описание.");

        var difficulty = Clamp(ReadInt(draftRoot, "difficulty") ?? request.Difficulty ?? 2, 1, 3);
        var rating = Math.Max(0, request.Rating ?? ReadInt(draftRoot, "rating") ?? 1);
        var tags = request.Tags ?? ReadString(draftRoot, "tags");
        var desiredSort = request.Sort;
        var suggestedAfterAssignmentId = request.AfterAssignmentId ?? ExtractPlacementAfterAssignmentId(draftRoot);
        var suggestedAfterTitle = ExtractPlacementAfterTitle(draftRoot);
        var publishingActorUserId = await ResolvePublishingActorUserIdAsync(courseId.Value, reviewedByUserId, ct);

        var canonicalOnly = string.Equals(ReadString(root, "schemaVersion") ?? ReadString(draftRoot, "schemaVersion") ?? string.Empty, "draft-v2", StringComparison.OrdinalIgnoreCase);

        var publishResult = await PublishDraftThroughApplicationServicesAsync(
            draftRoot,
            assignmentType,
            courseId.Value,
            publishingActorUserId,
            title,
            description,
            difficulty,
            rating,
            tags,
            desiredSort,
            suggestedAfterAssignmentId,
            canonicalOnly,
            ct);

        draft.Status = "published";
        draft.ReviewedByUserId = reviewedByUserId;
        draft.ReviewedAtUtc = DateTime.UtcNow;
        draft.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return new PublishAiDraftResultDto
        {
            DraftId = draft.Id,
            AssignmentId = publishResult.AssignmentId,
            CourseId = courseId.Value,
            AssignmentType = assignmentType,
            Title = title,
            PlacementAfterAssignmentId = suggestedAfterAssignmentId,
            PlacementAfterTitle = suggestedAfterTitle,
            PlacementApplied = publishResult.PlacementApplied,
        };
    }

    private async Task<(Guid AssignmentId, bool PlacementApplied)> PublishDraftThroughApplicationServicesAsync(
        JsonElement draftRoot,
        string assignmentType,
        Guid courseId,
        Guid actorUserId,
        string title,
        string description,
        int difficulty,
        int rating,
        string? tags,
        int? desiredSort,
        Guid? afterAssignmentId,
        bool canonicalOnly,
        CancellationToken ct)
    {
        var createRequest = BuildCreateAssignmentRequest(draftRoot, assignmentType, title, description, difficulty, rating, tags, canonicalOnly);
        var assignmentId = await _assignmentService.CreateAsync(courseId, createRequest, actorUserId);

        await _db.TaskAssignments
            .Where(x => x.Id == assignmentId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.IsAiGenerated, true)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);

        if (assignmentType == "test")
        {
            var dto = BuildTaskTestEditDto(draftRoot, canonicalOnly);
            await _taskTestService.SaveEditAsync(assignmentId, actorUserId, dto, ct);
        }
        else if (assignmentType == "math")
        {
            var dto = BuildTaskMathEditDto(draftRoot, canonicalOnly);
            await _taskMathService.SaveEditAsync(assignmentId, actorUserId, dto, ct);
        }

        var current = await _db.TaskAssignments.AsNoTracking()
            .Where(x => x.Id == assignmentId)
            .Select(x => (int?)x.Sort)
            .FirstOrDefaultAsync(ct);
        var placementApplied = false;
        if (afterAssignmentId.HasValue)
        {
            placementApplied = await _assignmentService.PlaceAfterAssignmentAsync(assignmentId, afterAssignmentId, actorUserId);
        }

        if (!placementApplied && desiredSort.HasValue && (current ?? -1) != desiredSort.Value)
            await _assignmentService.UpdateSortAsync(assignmentId, actorUserId, desiredSort.Value);

        return (assignmentId, placementApplied);
    }

    private async Task<Guid> ResolvePublishingActorUserIdAsync(Guid courseId, Guid reviewedByUserId, CancellationToken ct)
    {
        var course = await _db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == courseId, ct)
            ?? throw new ValidationException("Курс не найден. Проверь, что курс существует.");

        var reviewerIsOwner = course.OwnerId == reviewedByUserId
            || await _db.CourseOwners.AsNoTracking().AnyAsync(x => x.CourseId == courseId && x.UserId == reviewedByUserId, ct);

        return reviewerIsOwner ? reviewedByUserId : course.OwnerId;
    }

    private static JsonElement ExtractPublishableDraftRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("draft", out var draftNode) && draftNode.ValueKind == JsonValueKind.Object)
            return draftNode;
        return root;
    }


    private static Guid? ExtractPlacementAfterAssignmentId(JsonElement draftRoot)
    {
        var direct = ExtractGuid(draftRoot, "placementAfterAssignmentId");
        if (direct != null)
            return direct;

        var placement = GetPropertyOrNull(draftRoot, "placement");
        var nested = ExtractGuid(placement, "afterAssignmentId") ?? ExtractGuid(placement, "placementAfterAssignmentId");
        if (nested != null)
            return nested;

        var meta = GetPropertyOrNull(draftRoot, "meta");
        var recommended = GetPropertyOrNull(meta, "recommendedPlacement");
        return ExtractGuid(recommended, "afterAssignmentId")
            ?? ExtractGuid(recommended, "placementAfterAssignmentId")
            ?? ExtractGuid(meta, "placementAfterAssignmentId");
    }

    private static string? ExtractPlacementAfterTitle(JsonElement draftRoot)
    {
        var direct = ReadString(draftRoot, "placementAfterTitle") ?? ReadString(draftRoot, "afterAssignmentTitle");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        var placement = GetPropertyOrNull(draftRoot, "placement");
        var nested = ReadString(placement, "afterAssignmentTitle") ?? ReadString(placement, "placementAfterTitle") ?? ReadString(placement, "anchorTitle") ?? ReadString(placement, "title");
        if (!string.IsNullOrWhiteSpace(nested))
            return nested;

        var meta = GetPropertyOrNull(draftRoot, "meta");
        var recommended = GetPropertyOrNull(meta, "recommendedPlacement");
        return ReadString(recommended, "afterAssignmentTitle")
            ?? ReadString(recommended, "placementAfterTitle")
            ?? ReadString(recommended, "anchorTitle")
            ?? ReadString(meta, "placementAfterTitle");
    }

    private static CreateAssignmentRequest BuildCreateAssignmentRequest(
        JsonElement draftRoot,
        string assignmentType,
        string title,
        string description,
        int difficulty,
        int rating,
        string? tags,
        bool canonicalOnly)
    {
        var request = new CreateAssignmentRequest
        {
            Title = title,
            Description = description,
            Type = assignmentType,
            Difficulty = difficulty,
            Rating = rating,
            Tags = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim(),
        };

        if (assignmentType == "code-test")
        {
            var langs = NormalizeCodeLanguages(ReadStringArray(draftRoot, "allowedLanguages"));
            request.AllowedLanguages = langs.Count > 0 ? langs : null;

            if (canonicalOnly && HasAnyProperty(draftRoot, "codePolicy"))
                throw new ValidationException("draft-v2 code-test должен использовать root-level requiredCalls/forbiddenCalls, а не nested codePolicy.");

            var codePolicyNode = canonicalOnly ? default : GetPropertyOrNull(draftRoot, "codePolicy");
            request.CodeForbiddenCalls = ReadStringArray(codePolicyNode, "forbiddenCalls");
            if (request.CodeForbiddenCalls.Count == 0)
                request.CodeForbiddenCalls = ReadStringArray(draftRoot, "forbiddenCalls");

            request.CodeRequiredCalls = ReadStringArray(codePolicyNode, "requiredCalls");
            if (request.CodeRequiredCalls.Count == 0)
                request.CodeRequiredCalls = ReadStringArray(draftRoot, "requiredCalls");

            var publicTests = canonicalOnly
                ? ReadCreateTestCases(draftRoot, false, "publicTests")
                : ReadCreateTestCases(draftRoot, false, "publicTests", "tests");
            var hiddenTests = ReadCreateTestCases(draftRoot, true, "hiddenTests");
            if (publicTests.Count < 2) throw new ValidationException($"Нужно минимум 2 открытых теста (publicTests), сейчас: {publicTests.Count}.");
            if (hiddenTests.Count < 1) throw new ValidationException($"Нужен минимум 1 скрытый тест (hiddenTests), сейчас: {hiddenTests.Count}.");
            if (publicTests.Count + hiddenTests.Count < 5) throw new ValidationException($"Нужно минимум 5 тестов суммарно (publicTests + hiddenTests), сейчас: {publicTests.Count + hiddenTests.Count}.");
            request.TestCases = publicTests.Concat(hiddenTests).ToList();
        }

        return request;
    }

    private static TaskTestEditDto BuildTaskTestEditDto(JsonElement draftRoot, bool canonicalOnly)
    {
        if (canonicalOnly)
            ThrowIfDraftV2HasLegacyProperties(draftRoot, "allowedLanguages", "publicTests", "hiddenTests", "referenceSolutionPython", "codePolicy");

        var questionsNode = GetPropertyOrNull(draftRoot, "questions");
        var questions = questionsNode.ValueKind == JsonValueKind.Array ? questionsNode.EnumerateArray().ToList() : new List<JsonElement>();
        if (questions.Count == 0)
            throw new ValidationException("Test draft не содержит ни одного вопроса. Публикация остановлена.");

        var settingsNode = GetPropertyOrNull(draftRoot, "settings");
        return new TaskTestEditDto
        {
            Settings = new TaskTestSettingsDto
            {
                MaxAttempts = Math.Max(1, ReadInt(settingsNode, "maxAttempts") ?? 1),
                PassPercent = Clamp(ReadInt(settingsNode, "passPercent") ?? 60, 1, 100),
                ShuffleQuestions = ReadBool(settingsNode, "shuffleQuestions") ?? true,
                ShuffleAnswers = ReadBool(settingsNode, "shuffleAnswers") ?? true,
                AllowReview = ReadBool(settingsNode, "allowReview") ?? true,
                AttemptTimeLimitsSeconds = ReadNullableIntArray(settingsNode, "attemptTimeLimitsSeconds"),
            },
            Questions = questions.Select((node, index) => BuildTaskTestQuestionEditDto(node, index, canonicalOnly)).ToList(),
        };
    }

    private static TaskMathEditDto BuildTaskMathEditDto(JsonElement draftRoot, bool canonicalOnly)
    {
        if (canonicalOnly)
            ThrowIfDraftV2HasLegacyProperties(draftRoot, "allowedLanguages", "publicTests", "hiddenTests", "referenceSolutionPython", "codePolicy");

        var blocksNode = GetPropertyOrNull(draftRoot, "blocks");
        var blocks = blocksNode.ValueKind == JsonValueKind.Array ? blocksNode.EnumerateArray().ToList() : new List<JsonElement>();
        if (blocks.Count == 0)
            throw new ValidationException("Math draft не содержит ни одного блока. Публикация остановлена.");

        var settingsNode = GetPropertyOrNull(draftRoot, "settings");
        return new TaskMathEditDto
        {
            Settings = new TaskMathSettingsDto
            {
                MaxAttempts = Math.Max(1, ReadInt(settingsNode, "maxAttempts") ?? 1),
                PassPercent = Clamp(ReadInt(settingsNode, "passPercent") ?? 60, 1, 100),
                ShuffleBlocks = ReadBool(settingsNode, "shuffleBlocks") ?? false,
                AllowReview = ReadBool(settingsNode, "allowReview") ?? true,
                AttemptTimeLimitsSeconds = ReadNullableIntArray(settingsNode, "attemptTimeLimitsSeconds"),
            },
            Blocks = blocks.Select((node, index) => BuildTaskMathBlockEditDto(node, index, canonicalOnly)).ToList(),
        };
    }

    private static TaskTestQuestionEditDto BuildTaskTestQuestionEditDto(JsonElement node, int index, bool canonicalOnly)
    {
        if (canonicalOnly)
            ThrowIfDraftV2HasLegacyProperties(node, "questionType", "title", "correctKeys", "correct", "answers", "correctAnswers");

        var type = NormalizeTestQuestionType(canonicalOnly
            ? (ReadString(node, "type") ?? "single-choice")
            : (ReadString(node, "type") ?? ReadString(node, "questionType") ?? "single-choice"));
        var dto = new TaskTestQuestionEditDto
        {
            Id = Guid.Empty,
            Order = index,
            Type = type,
            Prompt = (canonicalOnly
                ? (ReadString(node, "prompt") ?? $"Вопрос {index + 1}")
                : (ReadString(node, "prompt") ?? ReadString(node, "title") ?? $"Вопрос {index + 1}")).Trim(),
        };

        if (type == "single-choice" || type == "multi-choice")
        {
            dto.Options = ReadTestOptions(node, "options");
            dto.CorrectOptionKeys = canonicalOnly
                ? ReadStringArray(node, "correctOptionKeys")
                : ReadStringArray(node, "correctOptionKeys", "correctKeys", "correct");
        }
        else
        {
            dto.AcceptedAnswers = canonicalOnly
                ? ReadStringArray(node, "acceptedAnswers")
                : ReadStringArray(node, "acceptedAnswers", "answers", "correctAnswers");
            dto.CaseSensitive = ReadBool(node, "caseSensitive") ?? false;
            dto.Trim = ReadBool(node, "trim") ?? true;
        }

        return dto;
    }

    private static TaskMathBlockEditDto BuildTaskMathBlockEditDto(JsonElement node, int index, bool canonicalOnly)
    {
        if (canonicalOnly)
            ThrowIfDraftV2HasLegacyProperties(node, "blockType", "title", "points", "promptContent", "answers", "correctAnswers", "items", "steps", "leftItems", "rightItems", "pairs");

        var kind = NormalizeMathKind(canonicalOnly
            ? (ReadString(node, "kind") ?? "info")
            : (ReadString(node, "kind") ?? ReadString(node, "blockType") ?? "info"));
        var dto = new TaskMathBlockEditDto
        {
            Id = Guid.Empty,
            Order = index,
            Kind = kind,
            Prompt = (canonicalOnly
                ? (ReadString(node, "prompt") ?? $"Блок {index + 1}")
                : (ReadString(node, "prompt") ?? ReadString(node, "title") ?? $"Блок {index + 1}")).Trim(),
            PromptContentJson = ExtractRichPromptContent(node, canonicalOnly),
            Score = Math.Max(0, canonicalOnly
                ? (ReadInt(node, "score") ?? (kind == "info" ? 0 : 1))
                : (ReadInt(node, "score") ?? ReadInt(node, "points") ?? (kind == "info" ? 0 : 1))),
            IsRequired = ReadBool(node, "isRequired") ?? true,
        };

        if (kind == "single-choice" || kind == "multi-choice")
        {
            dto.Options = ReadMathOptions(node, "options");
            dto.CorrectOptionKeys = canonicalOnly
                ? ReadStringArray(node, "correctOptionKeys")
                : ReadStringArray(node, "correctOptionKeys", "correctKeys", "correct");
        }
        else if (kind == "number" || kind == "expression" || kind == "set")
        {
            dto.AcceptedAnswers = canonicalOnly
                ? ReadStringArray(node, "acceptedAnswers")
                : ReadStringArray(node, "acceptedAnswers", "answers", "correctAnswers");
            dto.CaseSensitive = ReadBool(node, "caseSensitive") ?? false;
            dto.Trim = ReadBool(node, "trim") ?? true;
            dto.NumericTolerance = kind == "number"
                ? (canonicalOnly ? (ReadDouble(node, "numericTolerance") ?? 0d) : (ReadDouble(node, "numericTolerance") ?? ReadDouble(node, "tolerance") ?? 0d))
                : null;
        }
        else if (kind == "order")
        {
            dto.OrderItems = canonicalOnly
                ? ReadStringArray(node, "orderItems")
                : ReadStringArray(node, "orderItems", "items", "steps");
        }
        else if (kind == "match")
        {
            dto.MatchLeftItems = canonicalOnly
                ? ReadMathOptions(node, "matchLeftItems")
                : ReadMathOptions(node, "matchLeftItems", "leftItems");
            dto.MatchRightItems = canonicalOnly
                ? ReadMathOptions(node, "matchRightItems")
                : ReadMathOptions(node, "matchRightItems", "rightItems");
            dto.MatchPairs = canonicalOnly
                ? ReadMathMatchPairs(node, "matchPairs")
                : ReadMathMatchPairs(node, "matchPairs", "pairs");
        }

        return dto;
    }

    private static List<CreateTestCaseDto> ReadCreateTestCases(JsonElement node, bool isHidden, params string[] names)
    {
        foreach (var name in names)
        {
            var arr = GetPropertyOrNull(node, name);
            if (arr.ValueKind != JsonValueKind.Array) continue;
            var list = new List<CreateTestCaseDto>();
            foreach (var item in arr.EnumerateArray())
            {
                var input = NormalizeTestInput(ReadString(item, "input"));
                var output = ReadString(item, "expectedOutput") ?? ReadString(item, "output") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(input) && string.IsNullOrWhiteSpace(output)) continue;
                list.Add(new CreateTestCaseDto
                {
                    Input = input,
                    ExpectedOutput = output,
                    IsHidden = isHidden,
                });
            }
            return list;
        }
        return new List<CreateTestCaseDto>();
    }

    private static string NormalizeTestInput(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return NoInputSentinel;
        return raw;
    }

    private static List<TaskTestOptionDto> ReadTestOptions(JsonElement node, params string[] names)
        => ReadOptionItems(node, names).Select(x => new TaskTestOptionDto { Key = x.Key, Text = x.Text }).ToList();

    private static List<TaskMathOptionDto> ReadMathOptions(JsonElement node, params string[] names)
        => ReadOptionItems(node, names).Select(x => new TaskMathOptionDto { Key = x.Key, Text = x.Text }).ToList();

    private static List<TaskMathMatchPairDto> ReadMathMatchPairs(JsonElement node, params string[] names)
        => ReadPairItems(node, names).Select(x => new TaskMathMatchPairDto { LeftKey = x.LeftKey, RightKey = x.RightKey }).ToList();

    private static List<(string Key, string Text)> ReadOptionItems(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            var arr = GetPropertyOrNull(node, name);
            if (arr.ValueKind != JsonValueKind.Array) continue;
            var list = new List<(string Key, string Text)>();
            var idx = 0;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var optionText = item.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(optionText))
                        list.Add((idx.ToString(), optionText));
                    idx++;
                    continue;
                }

                var key = ReadString(item, "key") ?? idx.ToString();
                var text = ReadString(item, "text") ?? ReadString(item, "label") ?? ReadString(item, "title") ?? key;
                if (!string.IsNullOrWhiteSpace(text))
                    list.Add((key.Trim(), text.Trim()));
                idx++;
            }
            return list;
        }

        return new List<(string Key, string Text)>();
    }

    private static List<(string LeftKey, string RightKey)> ReadPairItems(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            var arr = GetPropertyOrNull(node, name);
            if (arr.ValueKind != JsonValueKind.Array) continue;
            var list = new List<(string LeftKey, string RightKey)>();
            foreach (var item in arr.EnumerateArray())
            {
                var leftKey = ReadString(item, "leftKey") ?? ReadString(item, "left") ?? string.Empty;
                var rightKey = ReadString(item, "rightKey") ?? ReadString(item, "right") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(leftKey) && !string.IsNullOrWhiteSpace(rightKey))
                    list.Add((leftKey.Trim(), rightKey.Trim()));
            }
            return list;
        }

        return new List<(string LeftKey, string RightKey)>();
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
    private static readonly HashSet<string> SupportedCodeLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "cpp",
        "csharp",
        "python",
        "javascript",
        "java",
        "pascal"
    };

    private static List<string> NormalizeCodeLanguages(IEnumerable<string> values)
    {
        var result = new List<string>();
        foreach (var raw in values ?? Array.Empty<string>())
        {
            var normalized = NormalizeCodeLanguage(raw);
            if (normalized == null || !SupportedCodeLanguages.Contains(normalized) || result.Exists(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(normalized);
        }
        return result;
    }

    private static string? NormalizeCodeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "py" or "python" => "python",
            "c++" or "cpp" => "cpp",
            "cs" or "c#" or "csharp" => "csharp",
            "js" or "javascript" => "javascript",
            "java" => "java",
            "pas" or "pascal" or "pascalabc" or "pascalabcnet" or "pascalabc.net" => "pascal",
            _ => null,
        };
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

    private static string? ExtractRichPromptContent(JsonElement node, bool canonicalOnly)
    {
        if (node.TryGetProperty("promptContentJson", out var existingJson) && existingJson.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(existingJson.GetString()) ? null : existingJson.GetString();

        if (!canonicalOnly && node.TryGetProperty("promptContent", out var richNode) && richNode.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
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
                var input = NormalizeTestInput(ReadString(item, "input"));
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

    private static void ThrowIfDraftV2HasLegacyProperties(JsonElement node, params string[] names)
    {
        var found = names.Where(name => HasAnyProperty(node, name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (found.Count > 0)
            throw new ValidationException($"draft-v2 содержит legacy-поля: {string.Join(", ", found)}. Исправь черновик на канонический контракт.");
    }

    private static bool HasAnyProperty(JsonElement node, string name)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out _);

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
