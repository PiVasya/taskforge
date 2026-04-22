using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO.AI;
using taskforge.Services.AI;
using taskforge.Services.Files;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Admin;

[ApiController]
[Route("api/admin/ai/chat")]
[Authorize(Roles = "Admin")]
public sealed class AdminAiChatController : ControllerBase
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".yaml", ".yml",
        ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".cpp", ".c", ".h", ".hpp", ".sql"
    };

    private static readonly HashSet<string> OfficeXmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx"
    };

    private readonly AiChatService _chat;
    private readonly IFileStorageService _storage;
    private readonly ICurrentUserService _current;

    public AdminAiChatController(AiChatService chat, IFileStorageService storage, ICurrentUserService current)
    {
        _chat = chat;
        _storage = storage;
        _current = current;
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(CancellationToken ct = default)
    {
        var data = await _chat.GetSessionsAsync(_current.GetUserId(), ct);
        return Ok(data);
    }

    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession([FromBody] AiFoundryChatCreateSessionRequestDto request, CancellationToken ct = default)
    {
        var data = await _chat.CreateSessionAsync(_current.GetUserId(), request, ct);
        return Ok(data);
    }

    [HttpGet("sessions/{id:guid}")]
    public async Task<IActionResult> GetSession(Guid id, CancellationToken ct = default)
    {
        var data = await _chat.GetSessionAsync(_current.GetUserId(), id, ct);
        return data == null ? NotFound() : Ok(data);
    }

    [HttpGet("sessions/{id:guid}/trace")]
    public async Task<IActionResult> GetTrace(Guid id, CancellationToken ct = default)
    {
        var data = await _chat.GetSessionTraceAsync(_current.GetUserId(), id, ct);
        return data == null ? NotFound() : Ok(data);
    }

    [HttpPut("sessions/{id:guid}")]
    public async Task<IActionResult> UpdateSession(Guid id, [FromBody] AiFoundryChatUpdateSessionRequestDto request, CancellationToken ct = default)
    {
        var data = await _chat.UpdateSessionAsync(_current.GetUserId(), id, request, ct);
        return data == null ? NotFound() : Ok(data);
    }

    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> DeleteSession(Guid id, CancellationToken ct = default)
    {
        var ok = await _chat.DeleteSessionAsync(_current.GetUserId(), id, ct);
        return ok ? NoContent() : NotFound();
    }

    [HttpPost("sessions/{id:guid}/messages")]
    public async Task<IActionResult> SendMessage(Guid id, [FromBody] AiFoundryChatSendMessageRequestDto request, CancellationToken ct = default)
    {
        var data = await _chat.SendMessageAsync(_current.GetUserId(), User?.Identity?.Name, id, request, ct);
        return data == null ? NotFound() : Ok(data);
    }

    [HttpPost("sessions/{id:guid}/confirm-tool")]
    public async Task<IActionResult> ConfirmTool(Guid id, [FromBody] AiFoundryChatConfirmToolRequestDto request, CancellationToken ct = default)
    {
        var data = await _chat.ConfirmToolCallAsync(_current.GetUserId(), User?.Identity?.Name, id, request, ct);
        return data == null ? NotFound() : Ok(data);
    }

    [HttpGet("sessions/{id:guid}/export")]
    public async Task<IActionResult> ExportSession(Guid id, [FromQuery] string? format = "md", CancellationToken ct = default)
    {
        var session = await _chat.GetSessionAsync(_current.GetUserId(), id, ct);
        if (session == null)
            return NotFound();

        var normalizedFormat = (format ?? "md").Trim().ToLowerInvariant();
        var fileNameBase = SanitizeFileName(session.Title);
        if (normalizedFormat == "json")
        {
            var json = SerializePrettyJson(session);
            return File(Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", $"{fileNameBase}.json");
        }

        if (normalizedFormat is "debug" or "zip")
        {
            var megaDebug = await _chat.GetSessionMegaDebugAsync(_current.GetUserId(), id, ct);
            var archive = BuildDebugExportArchive(megaDebug ?? new AiFoundryChatMegaDebugDto { Session = session, Trace = await _chat.GetSessionTraceAsync(_current.GetUserId(), id, ct) ?? new AiFoundryChatTraceResponseDto() });
            return File(archive, "application/zip", $"{fileNameBase}-mega-debug.zip");
        }

        var markdown = BuildMarkdownExport(session);
        return File(Encoding.UTF8.GetBytes(markdown), "text/markdown; charset=utf-8", $"{fileNameBase}.md");
    }

    [HttpPost("files")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> UploadFile([FromForm] IFormFile file, CancellationToken ct = default)
    {
        if (file == null || file.Length <= 0)
            return BadRequest(new { message = "Файл не передан" });

        await using var stream = file.OpenReadStream();
        await using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var ext = Path.GetExtension(file.FileName);
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        var key = await _storage.UploadBytesAsync(bytes, contentType, "ai-chat", string.IsNullOrWhiteSpace(ext) ? ".bin" : ext, ct);
        var url = $"/api/files/{Uri.EscapeDataString(key)}";

        return Ok(new AiFoundryChatAttachmentDto
        {
            FileKey = key,
            OriginalName = file.FileName,
            MimeType = contentType,
            PublicUrl = url,
            SizeBytes = file.Length,
            TextExcerpt = TryExtractTextExcerpt(file.FileName, contentType, bytes),
        });
    }

    private static string BuildMarkdownExport(AiFoundryChatSessionDto session)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {session.Title}");
        sb.AppendLine();
        sb.AppendLine($"- SessionId: `{session.Id}`");
        sb.AppendLine($"- CreatedAtUtc: `{session.CreatedAtUtc:O}`");
        sb.AppendLine($"- UpdatedAtUtc: `{session.UpdatedAtUtc:O}`");
        if (!string.IsNullOrWhiteSpace(session.CourseTitle) || session.CourseId.HasValue)
            sb.AppendLine($"- Course: `{session.CourseTitle ?? session.CourseId?.ToString()}`");
        sb.AppendLine();

        if (session.Memory != null)
        {
            sb.AppendLine("## Memory");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(session.Memory.Summary))
            {
                sb.AppendLine(session.Memory.Summary);
                sb.AppendLine();
            }

            foreach (var fact in session.Memory.Facts ?? new List<string>())
                sb.AppendLine($"- {fact}");
            if ((session.Memory.Facts?.Count ?? 0) > 0)
                sb.AppendLine();

            sb.AppendLine("### Snapshot");
            sb.AppendLine();
            sb.AppendLine($"- ActionMode: `{session.Memory.LastActionMode}`");
            sb.AppendLine($"- InstructionStrictness: `{session.Memory.InstructionStrictness}`");
            sb.AppendLine($"- PreferAutonomousCompletion: `{session.Memory.PreferAutonomousCompletion}`");
            sb.AppendLine($"- LatestIntentKind: `{session.Memory.LatestIntentKind ?? "—"}`");
            if (!string.IsNullOrWhiteSpace(session.Memory.LatestExplicitInstruction))
                sb.AppendLine($"- LatestExplicitInstruction: {session.Memory.LatestExplicitInstruction}");
            if (!string.IsNullOrWhiteSpace(session.Memory.LatestTeachingScript))
                sb.AppendLine($"- LatestTeachingScript: {session.Memory.LatestTeachingScript}");
            sb.AppendLine();

            if (session.Memory.AgentState != null)
            {
                sb.AppendLine("### AgentState");
                sb.AppendLine();
                sb.AppendLine($"- ObjectiveKind: `{session.Memory.AgentState.ObjectiveKind}`");
                sb.AppendLine($"- CurrentStage: `{session.Memory.AgentState.CurrentStage}`");
                sb.AppendLine($"- AutonomyMode: `{session.Memory.AgentState.AutonomyMode}`");
                sb.AppendLine($"- ConfidencePercent: `{session.Memory.AgentState.ConfidencePercent}`");
                if (!string.IsNullOrWhiteSpace(session.Memory.AgentState.ObjectiveSummary))
                    sb.AppendLine($"- ObjectiveSummary: {session.Memory.AgentState.ObjectiveSummary}");
                if (!string.IsNullOrWhiteSpace(session.Memory.AgentState.StageSummary))
                    sb.AppendLine($"- StageSummary: {session.Memory.AgentState.StageSummary}");
                if (!string.IsNullOrWhiteSpace(session.Memory.AgentState.NextSuggestedAction))
                    sb.AppendLine($"- NextSuggestedAction: `{session.Memory.AgentState.NextSuggestedAction}`");
                if ((session.Memory.AgentState.OpenQuestions?.Count ?? 0) > 0)
                    foreach (var question in session.Memory.AgentState.OpenQuestions)
                        sb.AppendLine($"- OpenQuestion: {question}");
                if ((session.Memory.AgentState.RiskFlags?.Count ?? 0) > 0)
                    foreach (var risk in session.Memory.AgentState.RiskFlags)
                        sb.AppendLine($"- RiskFlag: {risk}");
                sb.AppendLine();
            }

            if (session.Memory.CurrentDraftBlueprint != null && (session.Memory.CurrentDraftBlueprint.Proposals?.Count ?? 0) > 0)
            {
                sb.AppendLine("### CurrentDraftBlueprint");
                sb.AppendLine();
                sb.AppendLine($"- Revision: `{session.Memory.CurrentDraftBlueprint.Revision}`");
                sb.AppendLine($"- ApprovedForDraft: `{session.Memory.CurrentDraftBlueprint.ApprovedForDraft}`");
                sb.AppendLine($"- Summary: {session.Memory.CurrentDraftBlueprint.Summary}");
                sb.AppendLine();
                foreach (var proposal in session.Memory.CurrentDraftBlueprint.Proposals)
                {
                    sb.AppendLine($"#### {proposal.Title}");
                    sb.AppendLine();
                    sb.AppendLine($"- Difficulty: `{proposal.Difficulty}`");
                    sb.AppendLine($"- Placement: `{proposal.PlacementAfterTitle ?? "—"}`");
                    if (!string.IsNullOrWhiteSpace(proposal.Goal))
                        sb.AppendLine($"- Goal: {proposal.Goal}");
                    if (!string.IsNullOrWhiteSpace(proposal.ConditionPreview))
                        sb.AppendLine($"- ConditionPreview: {proposal.ConditionPreview}");
                    if (!string.IsNullOrWhiteSpace(proposal.FullCondition))
                    {
                        sb.AppendLine();
                        sb.AppendLine("```text");
                        sb.AppendLine(proposal.FullCondition);
                        sb.AppendLine("```");
                    }
                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine("## Transcript");
        sb.AppendLine();
        var turn = 0;
        foreach (var message in session.Messages ?? new List<AiFoundryChatMessageDto>())
        {
            turn++;
            var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "AI" : "User";
            sb.AppendLine($"### {turn}. {role} · {message.CreatedAtUtc:O}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(message.Status) || message.PendingJobId.HasValue)
            {
                sb.AppendLine($"- Status: `{message.Status ?? "—"}`");
                if (message.PendingJobId.HasValue)
                    sb.AppendLine($"- PendingJobId: `{message.PendingJobId}`");
                sb.AppendLine();
            }
            sb.AppendLine(string.IsNullOrWhiteSpace(message.Content) ? "_empty_" : message.Content);
            sb.AppendLine();

            foreach (var attachment in message.Attachments ?? new List<AiFoundryChatAttachmentDto>())
                sb.AppendLine($"- Attachment: {attachment.OriginalName ?? attachment.FileKey} ({attachment.MimeType ?? "file"}) {attachment.PublicUrl}");
            if ((message.Attachments?.Count ?? 0) > 0)
                sb.AppendLine();

            var toolCalls = (message.ToolCalls?.Count ?? 0) > 0 ? message.ToolCalls : (message.ToolCall == null ? new List<AiFoundryChatToolCallDto>() : new List<AiFoundryChatToolCallDto> { message.ToolCall });
            foreach (var tool in toolCalls)
            {
                sb.AppendLine($"- ToolCall: {tool.Name} :: {tool.Reason}");
                if (!string.IsNullOrWhiteSpace(tool.ArgumentsJson))
                {
                    sb.AppendLine();
                    sb.AppendLine("```json");
                    sb.AppendLine(TryPrettyJson(tool.ArgumentsJson));
                    sb.AppendLine("```");
                }
            }
            if (toolCalls.Count > 0)
                sb.AppendLine();

            var toolResults = (message.ToolResults?.Count ?? 0) > 0 ? message.ToolResults : (message.ToolResult == null ? new List<AiFoundryChatToolResultDto>() : new List<AiFoundryChatToolResultDto> { message.ToolResult });
            foreach (var result in toolResults)
            {
                sb.AppendLine($"- ToolResult: {result.Status} :: {result.Summary}");
                if (!string.IsNullOrWhiteSpace(result.NavigateTo))
                    sb.AppendLine($"  - NavigateTo: {result.NavigateTo}");
                if (result.JobId.HasValue)
                    sb.AppendLine($"  - JobId: `{result.JobId}`");
                if (result.BatchId.HasValue)
                    sb.AppendLine($"  - BatchId: `{result.BatchId}`");
                if (result.DraftId.HasValue)
                    sb.AppendLine($"  - DraftId: `{result.DraftId}`");
                if (result.AssignmentId.HasValue)
                    sb.AppendLine($"  - AssignmentId: `{result.AssignmentId}`");
                if (result.CourseId.HasValue)
                    sb.AppendLine($"  - CourseId: `{result.CourseId}`");
                if (result.RequiresConfirmation && result.ConfirmationToolCall != null)
                    sb.AppendLine($"  - RequiresConfirmation: `{result.ConfirmationToolCall.Name}`");
            }
            if (toolResults.Count > 0)
                sb.AppendLine();
        }

        return sb.ToString();
    }

    private static byte[] BuildDebugExportArchive(AiFoundryChatMegaDebugDto bundle)
    {
        var session = bundle?.Session ?? new AiFoundryChatSessionDto();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            AddZipEntry(archive, "transcript.md", BuildMarkdownExport(session));
            AddZipEntry(archive, "session.json", SerializePrettyJson(session));
            AddZipEntry(archive, "memory.json", SerializePrettyJson(session.Memory));

            var timeline = (session.Messages ?? new List<AiFoundryChatMessageDto>())
                .Select((message, index) => new
                {
                    turn = index + 1,
                    message.Id,
                    message.Role,
                    message.CreatedAtUtc,
                    message.Status,
                    message.PendingJobId,
                    message.Content,
                    attachments = message.Attachments,
                    toolCalls = (message.ToolCalls?.Count ?? 0) > 0 ? message.ToolCalls : (message.ToolCall == null ? new List<AiFoundryChatToolCallDto>() : new List<AiFoundryChatToolCallDto> { message.ToolCall }),
                    toolResults = (message.ToolResults?.Count ?? 0) > 0 ? message.ToolResults : (message.ToolResult == null ? new List<AiFoundryChatToolResultDto>() : new List<AiFoundryChatToolResultDto> { message.ToolResult }),
                })
                .ToList();
            AddZipEntry(archive, "timeline.json", SerializePrettyJson(timeline));
            AddZipEntry(archive, "trace.json", SerializePrettyJson(bundle.Trace));
            AddZipEntry(archive, "loop-diagnostics.json", SerializePrettyJson(bundle.LoopDiagnostics));
            AddZipEntry(archive, "mega-debug.json", SerializePrettyJson(bundle));

            var manifest = new
            {
                generatedAtUtc = DateTime.UtcNow,
                sessionId = session.Id,
                sessionTitle = session.Title,
                messageCount = session.Messages?.Count ?? 0,
                traceEvents = bundle.Trace?.Events?.Count ?? 0,
                linkedJobs = bundle.LinkedJobs?.Count ?? 0,
                linkedBatches = bundle.LinkedBatches?.Count ?? 0,
                linkedDrafts = bundle.LinkedDrafts?.Count ?? 0,
                suspectedLoop = bundle.LoopDiagnostics?.SuspectedLoop ?? false,
                files = new[]
                {
                    "transcript.md",
                    "session.json",
                    "memory.json",
                    "timeline.json",
                    "trace.json",
                    "loop-diagnostics.json",
                    "mega-debug.json",
                    "messages/*",
                    "jobs/*",
                    "batches/*",
                    "drafts/*"
                }
            };
            AddZipEntry(archive, "manifest.json", SerializePrettyJson(manifest));

            foreach (var item in timeline)
                AddZipEntry(archive, $"messages/{item.turn:D3}-{item.Role}.json", SerializePrettyJson(item));

            foreach (var job in bundle.LinkedJobs ?? new List<AiFoundryChatDebugJobDto>())
            {
                AddZipEntry(archive, $"jobs/{job.CreatedAtUtc:yyyyMMdd-HHmmss}-{job.Id}.json", SerializePrettyJson(job));
                AddZipEntry(archive, $"jobs/{job.Id}-input.json", TryPrettyJson(job.InputJson));
                AddZipEntry(archive, $"jobs/{job.Id}-result.json", TryPrettyJson(job.ResultJson));
                AddZipEntry(archive, $"jobs/{job.Id}-telemetry.json", TryPrettyJson(job.TelemetryJson));
            }

            foreach (var batch in bundle.LinkedBatches ?? new List<AiFoundryChatDebugBatchDto>())
                AddZipEntry(archive, $"batches/{batch.CreatedAtUtc:yyyyMMdd-HHmmss}-{batch.Id}.json", SerializePrettyJson(batch));

            foreach (var draft in bundle.LinkedDrafts ?? new List<AiFoundryChatDebugDraftDto>())
                AddZipEntry(archive, $"drafts/{draft.UpdatedAtUtc:yyyyMMdd-HHmmss}-{draft.Id}.json", SerializePrettyJson(draft));
        }

        return ms.ToArray();
    }

    private static void AddZipEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content ?? string.Empty);
    }

    private static string SerializePrettyJson(object? value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        });

    private static string TryPrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            });
        }
        catch
        {
            return json.Trim();
        }
    }

    private static string SanitizeFileName(string? raw)
    {
        var value = string.IsNullOrWhiteSpace(raw) ? "taskforge-ai-chat-history" : raw.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '-');
        value = Regex.Replace(value, @"\s+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(value) ? "taskforge-ai-chat-history" : value;
    }

    private static string? TryExtractTextExcerpt(string fileName, string? contentType, byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return null;

        var ext = Path.GetExtension(fileName ?? string.Empty);
        var normalizedExt = string.IsNullOrWhiteSpace(ext) ? string.Empty : ext.Trim();
        var normalizedContentType = (contentType ?? string.Empty).Trim();
        var isTextMime = normalizedContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedContentType, "application/json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedContentType, "application/xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedContentType, "application/x-yaml", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (string.Equals(normalizedExt, ".zip", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedContentType, "application/zip", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedContentType, "application/x-zip-compressed", StringComparison.OrdinalIgnoreCase))
                return FinalizeExcerpt(ExtractZipBundleText(bytes));

            if (OfficeXmlExtensions.Contains(normalizedExt))
                return FinalizeExcerpt(ExtractOfficeOpenXmlText(normalizedExt, bytes));

            if (string.Equals(normalizedExt, ".pdf", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                return FinalizeExcerpt(ExtractPdfLikeText(bytes));

            if (isTextMime || TextExtensions.Contains(normalizedExt))
                return FinalizeExcerpt(DecodeText(bytes));
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;

        var slice = bytes.Length <= 65536 ? bytes : bytes[..65536];
        var text = Encoding.UTF8.GetString(slice);
        if (text.Contains('\uFFFD'))
            text = Encoding.Unicode.GetString(slice);
        return text;
    }

    private static string? ExtractOfficeOpenXmlText(string ext, byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

        return ext.ToLowerInvariant() switch
        {
            ".docx" => ExtractDocxText(archive),
            ".xlsx" => ExtractXlsxText(archive),
            ".pptx" => ExtractPptxText(archive),
            _ => null,
        };
    }

    private static string? ExtractZipBundleText(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

        var parts = new List<string>();
        foreach (var entry in archive.Entries
                     .Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.Length > 0)
                     .OrderBy(x => x.FullName)
                     .Take(12))
        {
            var ext = Path.GetExtension(entry.Name ?? string.Empty);
            try
            {
                using var entryStream = entry.Open();
                using var entryMs = new MemoryStream();
                entryStream.CopyTo(entryMs);
                var entryBytes = entryMs.ToArray();
                var extracted = TryExtractTextExcerpt(entry.Name, null, entryBytes);
                if (!string.IsNullOrWhiteSpace(extracted))
                    parts.Add($"[{entry.FullName}] {extracted}");
                else if (TextExtensions.Contains(ext))
                    parts.Add($"[{entry.FullName}] {DecodeText(entryBytes)}");
            }
            catch
            {
                // ignore bad nested entry
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static string? ExtractDocxText(ZipArchive archive)
    {
        var candidates = archive.Entries
            .Where(x => x.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase)
                && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !x.FullName.Contains("rels", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.FullName)
            .Take(8)
            .ToList();

        var parts = new List<string>();
        foreach (var entry in candidates)
        {
            var xml = ReadZipEntry(entry);
            if (string.IsNullOrWhiteSpace(xml))
                continue;

            var doc = XDocument.Parse(xml);
            var textNodes = doc.Descendants().Where(x => x.Name.LocalName is "t" or "instrText").Select(x => x.Value);
            var combined = string.Join(" ", textNodes);
            if (!string.IsNullOrWhiteSpace(combined))
                parts.Add(combined);
        }

        return string.Join("\n", parts);
    }

    private static string? ExtractXlsxText(ZipArchive archive)
    {
        var sharedStrings = new List<string>();
        var sharedEntry = archive.GetEntry("xl/sharedStrings.xml");
        if (sharedEntry != null)
        {
            var sharedXml = ReadZipEntry(sharedEntry);
            if (!string.IsNullOrWhiteSpace(sharedXml))
            {
                var sharedDoc = XDocument.Parse(sharedXml);
                sharedStrings = sharedDoc.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value).ToList();
            }
        }

        var sheetEntries = archive.Entries
            .Where(x => x.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.FullName)
            .Take(5)
            .ToList();

        var rows = new List<string>();
        foreach (var entry in sheetEntries)
        {
            var xml = ReadZipEntry(entry);
            if (string.IsNullOrWhiteSpace(xml))
                continue;

            var doc = XDocument.Parse(xml);
            var rowTexts = doc.Descendants().Where(x => x.Name.LocalName == "row").Take(80).Select(row =>
            {
                var cellTexts = new List<string>();
                foreach (var cell in row.Elements().Where(x => x.Name.LocalName == "c"))
                {
                    var type = cell.Attribute("t")?.Value;
                    var raw = cell.Descendants().FirstOrDefault(x => x.Name.LocalName == "v")?.Value;
                    if (string.IsNullOrWhiteSpace(raw))
                        raw = cell.Descendants().FirstOrDefault(x => x.Name.LocalName == "t")?.Value;
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase) && int.TryParse(raw, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                        cellTexts.Add(sharedStrings[sharedIndex]);
                    else
                        cellTexts.Add(raw);
                }
                return string.Join(" | ", cellTexts.Where(x => !string.IsNullOrWhiteSpace(x)));
            });

            rows.AddRange(rowTexts.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        return string.Join("\n", rows);
    }

    private static string? ExtractPptxText(ZipArchive archive)
    {
        var slideEntries = archive.Entries
            .Where(x => x.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.FullName)
            .Take(10)
            .ToList();

        var slides = new List<string>();
        foreach (var entry in slideEntries)
        {
            var xml = ReadZipEntry(entry);
            if (string.IsNullOrWhiteSpace(xml))
                continue;

            var doc = XDocument.Parse(xml);
            var texts = doc.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value);
            var combined = string.Join(" ", texts);
            if (!string.IsNullOrWhiteSpace(combined))
                slides.Add(combined);
        }

        return string.Join("\n\n", slides);
    }

    private static string? ExtractPdfLikeText(byte[] bytes)
    {
        var slice = bytes.Length <= 65536 ? bytes : bytes[..65536];
        var latin = Encoding.Latin1.GetString(slice);
        var matches = Regex.Matches(latin, @"[\p{L}\p{N}\p{P}\p{Zs}]{5,}")
            .Cast<Match>()
            .Select(x => x.Value)
            .Where(x => x.Any(char.IsLetterOrDigit))
            .Select(x => x.Replace("\\n", " ").Replace("\\r", " "))
            .Take(300)
            .ToList();

        if (matches.Count == 0)
            return null;

        return string.Join(" ", matches);
    }

    private static string ReadZipEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? FinalizeExcerpt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Replace("\r", " ").Replace("\0", string.Empty);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (text.Length > 4000)
            text = text[..4000] + "...";
        return text;
    }
}
