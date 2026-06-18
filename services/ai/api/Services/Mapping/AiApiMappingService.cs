using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;

using TaskForge.Ai.Api.Contracts;
using TaskForge.Ai.Api.Hubs;
using static TaskForge.Ai.Api.Services.Access.AiApiAccessService;
using static TaskForge.Ai.Api.Services.Common.AiApiCommonService;
using static TaskForge.Ai.Api.Services.Results.AiApiResultsService;
using static TaskForge.Ai.Api.Services.Serialization.AiApiSerializationService;
using static TaskForge.Ai.Api.Services.Testing.AiApiTestingService;

namespace TaskForge.Ai.Api.Services.Mapping;

internal static class AiApiMappingService
{
    internal static async Task<TaskForge.Ai.Api.Domain.AiConversation?> GetConversationForUser(Guid conversationId, HttpContext http, IConfiguration cfg, AiDbContext db, bool asNoTracking)
    {
        var uid = CurrentUserId(http, cfg);
        if (uid == null) return null;

        var q = asNoTracking ? db.Conversations.AsNoTracking() : db.Conversations.AsQueryable();
        var conversation = await q.FirstOrDefaultAsync(x => x.Id == conversationId);
        if (conversation == null) return null;

        // Old orphan conversations must not be auto-adopted by the first user who knows the id.
        // Admin/editor can inspect them; normal users get no access until a migration assigns ownership.
        if (conversation.UserId == null)
        {
            return IsEditorOrAdmin(http) ? conversation : null;
        }

        if (conversation.UserId == uid.Value || IsEditorOrAdmin(http)) return conversation;
        return null;
    }

    internal static async Task<IResult> SaveAttachment(Guid conversationId, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, CancellationToken ct)
    {
        var c = await GetConversationForUser(conversationId, http, cfg, db, asNoTracking: false);
        if (c == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Диалог не найден.", code = "AI_CONVERSATION_NOT_FOUND" });
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.FirstOrDefault();
        if (file == null || file.Length == 0) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Файл не передан.", code = "AI_ATTACHMENT_REQUIRED" });
        if (file.Length > 8 * 1024 * 1024) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Файл слишком большой для AI-вложения. Максимум 8 MB.", code = "AI_ATTACHMENT_TOO_LARGE" }, statusCode: 413);
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var node = new JsonObject
        {
            ["fileName"] = file.FileName,
            ["contentType"] = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            ["size"] = file.Length,
            ["base64"] = Convert.ToBase64String(ms.ToArray())
        };
        var msg = new TaskForge.Ai.Api.Domain.AiMessage { ConversationId = conversationId, Role = "attachment", Content = node.ToJsonString() };
        db.Messages.Add(msg);
        c.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Microsoft.AspNetCore.Http.Results.Ok(new { attachment = ToMessageDto(msg), queued = false });
    }

    internal static async Task<IResult> ApplyArtifact(Guid? runId, Guid artifactId, JsonElement payload, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, IHttpClientFactory factory, CancellationToken ct)
    {
        var artifact = await db.Artifacts.FirstOrDefaultAsync(x => x.Id == artifactId && (!runId.HasValue || x.RunId == runId.Value), ct);
        if (artifact == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Материал ассистента не найден. Обновите диалог и попробуйте снова.", code = "AI_ARTIFACT_NOT_FOUND" });
        var c = await GetConversationForUser(artifact.ConversationId, http, cfg, db, asNoTracking: true);
        if (c == null) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этому материалу ассистента.", code = "AI_ARTIFACT_FORBIDDEN" }, statusCode: 403);
        var dryRun = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True;
        var data = JsonNode.Parse(artifact.DataJson) as JsonObject ?? new JsonObject();
        var type = artifact.Type.ToLowerInvariant();
        if (dryRun) return Microsoft.AspNetCore.Http.Results.Ok(new { dryRun = true, artifact = ToArtifactDto(artifact), operations = InferApplyOperations(artifact.Type, data), patchSet = BuildPatchPreview(data) });
        if (type.Contains("course_patch_set") || string.Equals(data["type"]?.ToString(), "course_patch_set", StringComparison.OrdinalIgnoreCase) || data["patches"] is JsonArray)
        {
            return await ApplyCoursePatchSet(artifact, data, request, factory, db, ct);
        }
        if (type.Contains("assignment") || data["assignmentType"] != null || data["title"] != null)
        {
            var courseId = GuidFromNode(data["courseId"]) ?? GuidFromPayload(payload, "courseId");
            if (courseId == null) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Для применения задания нужен courseId.", code = "AI_ARTIFACT_COURSE_REQUIRED" });
            var body = BuildAssignmentPayload(data, payload);
            var client = factory.CreateClient();
            ForwardAuth(request, client);
            var response = await client.PostAsJsonAsync($"http://tasks-api:8080/api/courses/{courseId}/assignments", body, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return Microsoft.AspNetCore.Http.Results.Content(raw, "application/json", statusCode: (int)response.StatusCode);
            artifact.Applied = true;
            artifact.AppliedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Content(raw, "application/json", statusCode: 200);
        }
        return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = $"Тип материала ассистента '{artifact.Type}' нельзя применить автоматически.", code = "AI_ARTIFACT_TYPE_UNSUPPORTED", artifact.Type });
    }

    internal static object[] InferApplyOperations(string type, JsonObject data)
    {
        var lower = type.ToLowerInvariant();
        if (lower.Contains("course_patch_set") || string.Equals(data["type"]?.ToString(), "course_patch_set", StringComparison.OrdinalIgnoreCase) || data["patches"] is JsonArray patches)
        {
            var patchArray = data["patches"] as JsonArray ?? new JsonArray();
            return patchArray.OfType<JsonObject>().Select(p => new
            {
                operation = p["operation"]?.ToString() ?? "update_assignment",
                assignmentId = p["assignmentId"]?.ToString(),
                title = p["title"]?.ToString(),
                changes = p["changes"] is JsonArray changes ? changes.Count : 0
            }).ToArray<object>();
        }
        if (lower.Contains("assignment") || data["assignmentType"] != null || data["title"] != null) return new object[] { new { operation = "create-assignment", title = data["title"]?.ToString() } };
        return Array.Empty<object>();
    }

    internal static async Task<IResult> ApplyCoursePatchSet(TaskForge.Ai.Api.Domain.AiArtifact artifact, JsonObject data, HttpRequest request, IHttpClientFactory factory, AiDbContext db, CancellationToken ct)
    {
        var patches = data["patches"] as JsonArray ?? new JsonArray();
        if (patches.Count == 0) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Patch set пустой.", code = "AI_PATCH_SET_EMPTY" });
        if (patches.Count > 250) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Слишком много правок за один раз. Максимум 250.", code = "AI_PATCH_SET_TOO_LARGE", count = patches.Count });

        var client = factory.CreateClient();
        ForwardAuth(request, client);
        var updated = new List<object>();
        var validations = new List<object>();

        foreach (var patch in patches.OfType<JsonObject>())
        {
            var assignmentIdText = patch["assignmentId"]?.ToString();
            if (!Guid.TryParse(assignmentIdText, out var assignmentId))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = $"Патч без корректного assignmentId: {patch["title"]}", code = "AI_PATCH_ASSIGNMENT_ID_REQUIRED" });

            var body = BuildAssignmentPatchPayload(patch);
            validations.Add(new { assignmentId, title = patch["title"]?.ToString(), changes = patch["changes"] is JsonArray ch ? ch.Count : 0 });
            var response = await client.PutAsJsonAsync($"http://tasks-api:8080/api/assignments/{assignmentId}", body, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return Microsoft.AspNetCore.Http.Results.Content(raw, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
            updated.Add(new { assignmentId, title = patch["title"]?.ToString(), response = ParseJson(raw) });
        }

        artifact.Applied = true;
        artifact.AppliedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, dryRun = false, updated, validations, message = $"Применено изменений: {updated.Count}." });
    }

    internal static object BuildAssignmentPatchPayload(JsonObject patch)
    {
        var body = new JsonObject();
        if (patch["changes"] is JsonArray changes)
        {
            foreach (var change in changes.OfType<JsonObject>())
            {
                var field = (change["field"]?.ToString() ?? string.Empty).Trim();
                var value = change["newValue"]?.DeepClone();
                switch (field.ToLowerInvariant())
                {
                    case "title": body["title"] = value; break;
                    case "description": body["description"] = value; break;
                    case "type": body["type"] = value; break;
                    case "language": body["language"] = value; break;
                    case "tags": body["tags"] = value; break;
                    case "difficulty": body["difficulty"] = value; break;
                    case "rating": body["rating"] = value; break;
                    case "starterCode": body["starterCode"] = value; break;
                    case "isVisible":
                    case "isvisible": body["isVisible"] = value; break;
                    case "codeForbiddenCalls": body["codeForbiddenCalls"] = value; break;
                    case "codeRequiredCalls": body["codeRequiredCalls"] = value; break;
                    default: throw new InvalidOperationException($"Поле '{field}' нельзя применить через patch set.");
                }
            }
        }
        return JsonSerializer.Deserialize<object>(body.ToJsonString(), JsonOptions()) ?? new { };
    }

    internal static object BuildAssignmentPayload(JsonObject data, JsonElement payload)
    {
        var title = data["title"]?.ToString() ?? "Задание от ассистента";
        var description = data["description"]?.ToString() ?? data["statement"]?.ToString();
        var language = data["language"]?.ToString() ?? "cpp";
        var assignmentType = data["assignmentType"]?.ToString() ?? data["type"]?.ToString() ?? "code-test";
        JsonNode? tests = null;
        if (data["publicTests"] is JsonArray pub || data["hiddenTests"] is JsonArray)
        {
            tests = new JsonObject
            {
                ["publicTests"] = data["publicTests"]?.DeepClone(),
                ["hiddenTests"] = data["hiddenTests"]?.DeepClone()
            };
        }
        return new { title, description, type = assignmentType, language, starterCode = data["starterCode"]?.ToString() ?? data["referenceSolution"]?.ToString(), tests, isVisible = false };
    }

    internal static void ForwardAuth(HttpRequest request, HttpClient client)
    {
        if (request.Headers.TryGetValue("Authorization", out var auth)) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth.ToString());
        if (request.Headers.TryGetValue("Cookie", out var cookie)) client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie.ToString());
    }

    internal static JsonNode? BuildCourseOutline(JsonNode? assignments)
    {
        var array = ExtractArray(assignments);
        if (array == null) return null;

        var outline = new JsonArray();
        foreach (var item in array.Take(80))
        {
            if (item is not JsonObject obj) continue;
            outline.Add(new JsonObject
            {
                ["id"] = obj["id"]?.ToString() ?? obj["Id"]?.ToString(),
                ["title"] = obj["title"]?.ToString() ?? obj["Title"]?.ToString(),
                ["type"] = obj["type"]?.ToString() ?? obj["assignmentType"]?.ToString() ?? obj["AssignmentType"]?.ToString(),
                ["order"] = obj["order"]?.DeepClone() ?? obj["Order"]?.DeepClone()
            });
        }
        return outline;
    }

    internal static AiStep BuildStep(AiRun run, AgentStepRequest request, int seq)
    {
        var step = request.Step;
        var title = ReadStepString(step, "title", ReadStepString(step, "message", "AI обрабатывает запрос"));
        var kind = ReadStepString(step, "kind", "worker");
        var status = ReadStepString(step, "status", "running");
        var actionName = ReadStepString(step, "actionName", ReadStepString(step, "action", "agent_step"));
        var summary = ReadStepString(step, "summary", string.Empty);
        var visible = ReadStepBool(step, "isVisibleToUser", true);

        return new AiStep
        {
            RunId = run.Id,
            ConversationId = run.ConversationId,
            Seq = seq,
            Kind = string.IsNullOrWhiteSpace(kind) ? "worker" : kind,
            Status = string.IsNullOrWhiteSpace(status) ? "running" : status,
            ActionName = string.IsNullOrWhiteSpace(actionName) ? "agent_step" : actionName,
            Title = string.IsNullOrWhiteSpace(title) ? "AI обрабатывает запрос" : title,
            Summary = string.IsNullOrWhiteSpace(summary) ? null : summary,
            DataJson = step.HasValue ? step.Value.GetRawText() : null,
            IsVisibleToUser = visible
        };
    }

    internal static object ToConversationDto(AiConversation x) => new
    {
        x.Id,
        x.Title,
        x.UserId,
        x.CourseId,
        x.AssignmentId,
        x.SupportTicketId,
        x.CreatedAtUtc,
        x.UpdatedAtUtc
    };

    internal static object ToMessageDto(AiMessage x, IReadOnlyList<AiArtifact>? artifacts = null) => new
    {
        x.Id,
        x.ConversationId,
        x.RunId,
        x.Role,
        text = x.Content,
        content = x.Content,
        x.Content,
        data = artifacts is { Count: > 0 } ? new { artifacts = artifacts.Select(ToArtifactDto).ToList() } : null,
        x.ClientMessageId,
        x.CreatedAtUtc
    };

    internal static object ToStepDto(AiStep x) => new
    {
        x.Id,
        x.RunId,
        x.ConversationId,
        x.Seq,
        x.Kind,
        x.Status,
        x.ActionName,
        x.Title,
        x.Summary,
        data = ParseJson(x.DataJson),
        x.IsVisibleToUser,
        x.CreatedAtUtc
    };

    internal static object ToArtifactDto(AiArtifact x) => new
    {
        x.Id,
        artifactId = x.Id,
        x.RunId,
        x.ConversationId,
        x.Type,
        x.Title,
        data = ParseJson(x.DataJson),
        x.Applied,
        x.AppliedAtUtc,
        x.CreatedAtUtc
    };

}
