using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TaskForge.AiAgent.Contracts;

namespace TaskForge.AiAgent.Workflows.Executors;

internal static partial class CourseSkillAnalyzer
{
    public static JsonObject BuildSkillMapInput(JsonElement payload, string userText, int maxAssignments = 120)
    {
        var root = new JsonObject
        {
            ["userRequest"] = userText,
            ["courseId"] = payload.GetPropertyOrDefault("courseId").ToString(),
            ["course"] = CloneOrNull(payload.GetPropertyOrDefault("course")),
            ["selectedCourse"] = CloneOrNull(payload.GetPropertyOrDefault("courseDigest").GetPropertyOrDefault("selectedCourse")),
            ["targetConcepts"] = CloneOrNull(payload.GetPropertyOrDefault("targetConcepts")),
            ["note"] = "This is the only context the skill-map step should use. Ignore courseCatalog/courseContexts from the full payload. assignments excludes hidden/AI drafts; existingAiDrafts are warnings only and must not count as acquired student skills. Metadata hints are weak and must never be the only placement evidence."
        };

        var assignments = new JsonArray();
        var existingAiDrafts = new JsonArray();
        var seen = new HashSet<Guid>();
        foreach (var item in EnumeratePreferredAssignments(payload))
        {
            if (assignments.Count >= maxAssignments) break;
            var id = GetGuid(item, "id", "assignmentId");
            var title = GetString(item, "title", "name");
            if (!id.HasValue || string.IsNullOrWhiteSpace(title)) continue;
            if (!seen.Add(id.Value)) continue;
            if (LooksLikeCourseCatalogItem(item)) continue;

            var isHidden = GetBool(item, "isHidden") == true;
            var isAiDraft = GetBool(item, "isAiDraft") == true;
            var descriptionPreview = Preview(PlainText(GetString(item, "descriptionPreview", "description", "condition", "body")), 700);
            var row = new JsonObject
            {
                ["assignmentId"] = id.Value.ToString(),
                ["position"] = GetInt(item, "index", "position", "sort") ?? assignments.Count,
                ["sort"] = GetInt(item, "sort", "order"),
                ["title"] = title,
                ["type"] = GetString(item, "type", "assignmentType"),
                ["difficulty"] = GetInt(item, "difficulty"),
                ["rating"] = GetInt(item, "rating"),
                ["tags"] = GetString(item, "tags"),
                ["allowedLanguages"] = CloneOrNull(item.GetPropertyOrDefault("allowedLanguages")),
                ["descriptionPreview"] = descriptionPreview,
                ["assignmentTextForStudy"] = Preview($"{title} {descriptionPreview}", 1200),
                ["weakMetadataConceptHints"] = CloneOrNull(item.GetPropertyOrDefault("conceptHints")),
                ["contentSummary"] = CloneOrNull(item.GetPropertyOrDefault("contentSummary")),
                ["testCases"] = CompactTestCases(item.GetPropertyOrDefault("testCases")),
                ["isHidden"] = isHidden,
                ["isAiDraft"] = isAiDraft
            };

            // AI drafts are useful as dedupe/context warnings, but they must not become
            // the canonical course sequence used for skill-map reasoning. Otherwise the
            // agent learns from its own previous mistakes and rejects/places new drafts
            // based on stale hidden material.
            if (isHidden || isAiDraft)
            {
                existingAiDrafts.Add(new JsonObject
                {
                    ["assignmentId"] = id.Value.ToString(),
                    ["position"] = row["position"]?.DeepClone(),
                    ["title"] = title,
                    ["descriptionPreview"] = descriptionPreview,
                    ["reason"] = isAiDraft ? "existing AI draft; do not count as acquired student skill" : "hidden assignment; do not count as canonical visible course step"
                });
                continue;
            }

            assignments.Add(row);
        }

        root["assignments"] = assignments;
        root["existingAiDrafts"] = existingAiDrafts;
        root["assignmentCount"] = assignments.Count;
        root["fallbackWarning"] = assignments.Count == 0
            ? "No assignments were found in courseDigest/courseOutline/focusAssignments. Do not invent an anchor."
            : null;
        return root;
    }


    public static CourseSkillBridgeContext FromModelMap(JsonElement payload, string userText, string modelText, CourseSkillBridgeContext fallback)
    {
        try
        {
            var json = ExtractJson(modelText);
            if (string.IsNullOrWhiteSpace(json))
                return fallback with { Source = "static-fallback", AnchorReason = "LLM course skill map returned no JSON." };

            if (JsonNode.Parse(json) is not JsonObject root)
                return fallback with { Source = "static-fallback", AnchorReason = "LLM course skill map root was not an object." };

            var anchor = root["anchor"] as JsonObject;
            var validAssignmentIds = CollectAssignmentIds(payload);
            var beforeId = ParseGuid(anchor?["insertBeforeAssignmentId"]?.ToString() ?? root["insertBeforeAssignmentId"]?.ToString());
            if (beforeId.HasValue && validAssignmentIds.Count > 0 && !validAssignmentIds.Contains(beforeId.Value))
                beforeId = null;

            var requestedSkills = ReadStringArrayOrFallback(root["requestedSkills"], fallback.RequestedSkills);
            var acquiredSkills = ReadStringArrayOrFallback(root["acquiredSkillsBeforeAnchor"], fallback.AcquiredSkills);
            var targetSkills = ReadStringArrayOrFallback(root["targetSkillsAtAnchor"] ?? root["targetSkills"], fallback.TargetSkills);
            var missingSkills = ReadStringArrayOrFallback(root["missingBridgeSkills"] ?? root["missingSkills"], fallback.MissingBridgeSkills);
            var bridgePlan = NormalizeBridgePlan(ReadObjectArray(root["bridgePlan"]).ToList()).ToList();
            var confidence = ReadDouble(anchor?["confidence"] ?? root["confidence"] ?? root["anchorConfidence"]);
            if (confidence.HasValue && confidence.Value < 0.6)
            {
                beforeId = null;
                bridgePlan.Clear();
            }

            if (missingSkills.Count == 0 && bridgePlan.Count > 0)
            {
                missingSkills = bridgePlan
                    .SelectMany(step => ReadStringArray(step["introducedSkills"]))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return new CourseSkillBridgeContext(
                BeforeAssignmentId: beforeId,
                PreviousTitle: anchor?["previousAssignmentTitle"]?.ToString() ?? root["previousAssignmentTitle"]?.ToString() ?? fallback.PreviousTitle,
                AnchorTitle: anchor?["anchorAssignmentTitle"]?.ToString() ?? root["anchorAssignmentTitle"]?.ToString() ?? fallback.AnchorTitle,
                RequestedSkills: requestedSkills,
                AcquiredSkills: acquiredSkills,
                TargetSkills: targetSkills,
                MissingBridgeSkills: missingSkills,
                Neighborhood: BuildModelNeighborhood(root, fallback),
                IsBridgeRequest: fallback.IsBridgeRequest || bridgePlan.Count > 0,
                BridgePlan: bridgePlan,
                Source: "llm-course-skill-map",
                AnchorReason: anchor?["reason"]?.ToString() ?? root["reason"]?.ToString(),
                RawModelMap: BuildCompactModelMap(root));
        }
        catch (Exception ex)
        {
            return fallback with { Source = "static-fallback", AnchorReason = $"LLM course skill map parse failed: {ex.GetType().Name}: {ex.Message}" };
        }
    }

}
