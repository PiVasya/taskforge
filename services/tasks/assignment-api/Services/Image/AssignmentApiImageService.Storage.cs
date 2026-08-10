using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;

using TaskForge.Tasks.Api.Contracts;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Services.Image;

internal static partial class AssignmentApiImageService
{
    internal static async Task<Guid?> SaveImageSolutionAsync(Guid assignmentId, Guid userId, string language, string code, int similarityPercent, bool passed, JsonElement result, IConfiguration cfg, IHttpClientFactory clients)
    {
        var baseUrl = ServiceUrl(cfg, "SolutionsApi", "http://solutions-api:8080");
        var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/internal/image-solutions")
        {
            Content = JsonContent.Create(new InternalImageSolutionRequest(userId, assignmentId, language, code, similarityPercent, passed, result), options: JsonOptions())
        };
        AddInternalKey(msg, cfg);

        try
        {
            using var response = await client.SendAsync(msg);
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(raw)) return null;
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("id", out var idProp) && Guid.TryParse(idProp.ToString(), out var savedId) ? savedId : null;
        }
        catch
        {
            return null;
        }
    }

    internal static async Task<LoadedImageBytes> LoadExpectedImageAsync(ImageTestCaseSpec test, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(test.ExpectedImageKey))
        {
            var client = clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var url = $"{ServiceUrl(cfg, "FilesApi", "http://files-api:8080")}/api/internal/files/{EscapeFileKeyForUrl(test.ExpectedImageKey)}";
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            AddInternalKey(msg, cfg);
            using var response = await client.SendAsync(msg, ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Не удалось получить эталонную картинку из MinIO/files-api: {(int)response.StatusCode} {System.Text.Encoding.UTF8.GetString(bytes)}");
            var contentType = response.Content.Headers.ContentType?.MediaType ?? test.ExpectedImageContentType ?? "image/png";
            return new LoadedImageBytes(bytes, contentType, test.ExpectedImageFileName ?? Path.GetFileName(test.ExpectedImageKey));
        }

        if (!string.IsNullOrWhiteSpace(test.ExpectedImageBase64))
        {
            var decoded = DecodeImageBase64(test.ExpectedImageBase64, test.ExpectedImageContentType ?? "image/png");
            return new LoadedImageBytes(decoded.Bytes, decoded.ContentType, test.ExpectedImageFileName ?? "expected.png");
        }

        throw new InvalidOperationException("У image-test кейса нет expectedImageKey и legacy expectedImageBase64.");
    }

    internal static async Task<UploadedFileDto> UploadImageBytesToFilesApiAsync(IHttpClientFactory clients, IConfiguration cfg, byte[] bytes, string? fileName, string contentType, string folder, CancellationToken ct)
    {
        if (bytes.Length == 0) throw new InvalidOperationException("Нельзя загрузить пустую эталонную картинку.");
        var safeFileName = string.IsNullOrWhiteSpace(fileName) ? $"image{ExtensionForContentType(contentType)}" : Path.GetFileName(fileName);
        var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        using var mp = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        mp.Add(part, "file", safeFileName);
        mp.Add(new StringContent(folder), "folder");
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl(cfg, "FilesApi", "http://files-api:8080")}/api/internal/files/images") { Content = mp };
        AddInternalKey(msg, cfg);
        using var response = await client.SendAsync(msg, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"files-api не сохранил expected image в MinIO: {(int)response.StatusCode} {raw}");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var key = root.TryGetProperty("key", out var k) ? k.GetString() : null;
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("files-api не вернул key после загрузки expected image.");
        var privateUrl = root.TryGetProperty("privateUrl", out var pu) ? pu.GetString() : PrivateFileUrl(key);
        var returnedContentType = root.TryGetProperty("contentType", out var ctProp) ? ctProp.GetString() : contentType;
        var returnedFileName = root.TryGetProperty("fileName", out var fn) ? fn.GetString() : safeFileName;
        var size = root.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : bytes.Length;
        return new UploadedFileDto(key, privateUrl, returnedContentType ?? contentType, returnedFileName ?? safeFileName, size);
    }

    internal static string PrivateFileUrl(string key) => $"/api/private-files/{Uri.EscapeDataString(key)}";

    internal static string EscapeFileKeyForUrl(string key) => string.Join("/", (key ?? string.Empty).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    internal static async Task<IResult> CompareImageUpload(Guid assignmentId, HttpRequest req, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients)
    {
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
        if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, CancellationToken.None)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
        var root = JsonNode.Parse(assignment.TestsJson ?? "{}") as JsonObject ?? new JsonObject();
        var referenceKey = NodeString(root, "imageTestReferenceKey") ?? NodeString(root, "expectedImageKey") ?? NodeString(root, "referenceKey") ?? NodeString(root, "imageKey");
        var referenceBase64 = StripDataUrl(NodeString(root, "referenceBase64") ?? NodeString(root, "expectedImageBase64") ?? NodeString(root, "imageBase64"));
        if (string.IsNullOrWhiteSpace(referenceKey) && string.IsNullOrWhiteSpace(referenceBase64)) return Problem(400, "IMAGE_REFERENCE_MISSING", "image-test.reference", "Для задания ещё не загружена эталонная картинка.");
        var form = await req.ReadFormAsync();
        var actual = form.Files.FirstOrDefault();
        if (actual == null || actual.Length == 0) return Problem(400, "IMAGE_ACTUAL_REQUIRED", "request.validation", "Выберите изображение для сравнения.");
        if (actual.Length > MaxImageUploadBytes(cfg)) return Problem(413, "IMAGE_ACTUAL_TOO_LARGE", "request.validation", $"Изображение для сравнения слишком большое. Максимум: {MaxImageUploadBytes(cfg) / 1024 / 1024} МБ.");
        var expectedSpec = new ImageTestCaseSpec("Основной тест", string.Empty, string.Empty, referenceBase64, referenceKey, JsonInt(assignment.TestsJson, "imageTestSimilarityThreshold", 90), false, NodeString(root, "referenceContentType") ?? "image/png", NodeString(root, "referenceFileName") ?? "expected.png");
        var expected = await LoadExpectedImageAsync(expectedSpec, clients, cfg, CancellationToken.None);
        await using var actualMs = new MemoryStream();
        await actual.CopyToAsync(actualMs);
        var userId = RequireUser(http, cfg);
        if (userId == null) return Unauthorized();
        if (await ConsumeTaskEnergyAsync(http, cfg, clients, userId.Value, "image-compare-upload", http.RequestAborted) is { } quotaProblem) return quotaProblem;
        var thresholdPercent = JsonInt(assignment.TestsJson, "imageTestSimilarityThreshold", 90);
        var threshold = System.Math.Clamp(thresholdPercent / 100.0, 0.0, 1.0);
        try
        {
            var client = clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            using var mp = new MultipartFormDataContent();
            var expectedPart = new ByteArrayContent(expected.Bytes);
            expectedPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(expected.ContentType ?? "application/octet-stream");
            mp.Add(expectedPart, "expected", expected.FileName ?? "expected.png");
            var actualPart = new ByteArrayContent(actualMs.ToArray());
            actualPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(actual.ContentType) ? "application/octet-stream" : actual.ContentType);
            mp.Add(actualPart, "actual", actual.FileName);
            var response = await client.PostAsync($"http://image-analyzer:8080/compare?threshold={threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}", mp);
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                await RefundTaskEnergyAsync(http, cfg, clients, userId.Value, "image-compare-upload-analyzer-failed", CancellationToken.None);
                return Problem(503, "IMAGE_ANALYZER_FAILED", "image-test.analyzer", "image-analyzer не смог сравнить изображения. Проверьте контейнер image-analyzer и формат файлов.", raw);
            }
            var json = JsonSerializer.Deserialize<JsonElement>(raw);
            var combined = json.TryGetProperty("combined_similarity", out var c) && c.TryGetDouble(out var cv) ? cv : 0.0;
            var clip = json.TryGetProperty("clip_similarity", out var cl) && cl.TryGetDouble(out var clv) ? clv : combined;
            var passed = json.TryGetProperty("passed", out var p) && p.ValueKind == JsonValueKind.True;
            return Microsoft.AspNetCore.Http.Results.Ok(new { passed, similarity = System.Math.Round(combined * 100, 2), clipSimilarity = System.Math.Round(clip * 100, 2), threshold = thresholdPercent, analyzer = json, referenceUrl = IsEditor(http, cfg) ? expectedSpec.ExpectedImageUrl : null });
        }
        catch (Exception ex)
        {
            await RefundTaskEnergyAsync(http, cfg, clients, userId.Value, "image-compare-upload-failed", CancellationToken.None);
            return Problem(503, "IMAGE_COMPARE_FAILED", "image-test.analyzer", "Не удалось выполнить сравнение через image-analyzer.", ex.Message);
        }
    }

    internal static long MaxImageUploadBytes(IConfiguration cfg) => cfg.GetValue<long?>("Files:MaxUploadBytes") ?? cfg.GetValue<long?>("Files:MaxImageUploadBytes") ?? 10L * 1024 * 1024;
}
