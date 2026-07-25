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
    internal static async Task<IResult> RenderImageCode(Guid assignmentId, ImageCodeRequest request, HttpContext http, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg)
    {
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
        if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, clients, CancellationToken.None)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
        var lang = NormalizeImageLanguage(request.Language ?? assignment.Language);
        var runner = ImageRunnerService(lang);
        if (runner == null) return Problem(400, "IMAGE_LANGUAGE_UNSUPPORTED", "image-test.run-code", "Image-runner доступен для C++/GLUT, C++ Turtle, Pascal GraphABC, Python Turtle и Python matplotlib/Pillow.", lang);
        var policyProblem = await AnalyzeCodePolicyForAssignment(assignment, lang, request.Code ?? string.Empty, clients, cfg, "image-test.run-code");
        if (policyProblem != null) return policyProblem;
        var userId = RequireUser(http, cfg);
        if (userId == null) return Unauthorized();
        if (await ConsumeTaskEnergyAsync(http, cfg, clients, userId.Value, "image-run-code", http.RequestAborted) is { } quotaProblem) return quotaProblem;
        try
        {
            var client = clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(90);
            var response = await client.PostAsJsonAsync($"http://{runner}:8000/render/debug", new { source = request.Code ?? string.Empty, stdin = request.Input, timeoutSeconds = request.TimeoutSeconds ?? 20, debug = true });
            var raw = await response.Content.ReadAsStringAsync();
            return Microsoft.AspNetCore.Http.Results.Content(raw, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            await RefundTaskEnergyAsync(http, cfg, clients, userId.Value, "image-run-code-failed", CancellationToken.None);
            return Problem(503, "IMAGE_RUNNER_FAILED", "image-test.run-code", "Не удалось запустить image-runner.", ex.Message);
        }
    }

    internal static async Task<IResult> CompareImageCode(Guid assignmentId, ImageCodeRequest request, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, bool submit, HttpContext? context = null)
    {
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
        if (assignment == null || (context is not null && !await CanUserAccessAssignmentAsync(assignment, context, cfg, clients, CancellationToken.None))) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
        var currentUserId = context is not null ? RequireUser(context, cfg) : null;
        if (context is not null && currentUserId == null) return Unauthorized();

        var root = JsonNode.Parse(assignment.TestsJson ?? "{}") as JsonObject ?? new JsonObject();
        var lang = NormalizeImageLanguage(request.Language ?? assignment.Language);
        var runner = ImageRunnerService(lang);
        if (runner == null) return Problem(400, "IMAGE_LANGUAGE_UNSUPPORTED", submit ? "image-test.submit-code" : "image-test.compare-code", "Image-runner доступен для C++/GLUT, C++ Turtle, Pascal GraphABC, Python Turtle и Python matplotlib/Pillow.", lang);

        var cases = ReadImageTestCases(root, request.Input);
        if (cases.Count == 0) return Problem(400, "IMAGE_REFERENCE_MISSING", "image-test.reference", "Для задания не настроены image-тесты: добавьте Input, Expected output и Expected image хотя бы для одного теста.");

        var policyProblem = await AnalyzeCodePolicyForAssignment(assignment, lang, request.Code ?? string.Empty, clients, cfg, submit ? "image-test.submit-code" : "image-test.compare-code");
        if (policyProblem != null) return policyProblem;
        if (context is not null && currentUserId.HasValue && await ConsumeTaskEnergyAsync(context, cfg, clients, currentUserId.Value, submit ? "image-submit-code" : "image-compare-code", context.RequestAborted) is { } quotaProblem) return quotaProblem;

        try
        {
            var client = clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(System.Math.Clamp((request.TimeoutSeconds ?? 20) * System.Math.Max(1, cases.Count) + 60, 90, 300));
            var runnerTimeout = request.TimeoutSeconds ?? 20;
            var results = new List<ImageCaseResult>();
            var canViewReferenceImages = context != null && IsEditor(context, cfg);

            for (var i = 0; i < cases.Count; i++)
            {
                var test = cases[i];
                var render = await client.PostAsJsonAsync($"http://{runner}:8000/render/debug", new { source = request.Code ?? string.Empty, stdin = test.Input, timeoutSeconds = runnerTimeout, debug = true });
                var renderRaw = await render.Content.ReadAsStringAsync();
                if (!render.IsSuccessStatusCode)
                {
                    results.Add(new ImageCaseResult(
                        Index: i + 1,
                        Name: test.Name,
                        Input: test.Input,
                        ExpectedOutput: test.IsHidden ? null : test.ExpectedOutput,
                        ActualOutput: null,
                        StdoutPassed: string.IsNullOrWhiteSpace(test.ExpectedOutput) ? true : false,
                        ImagePassed: false,
                        Passed: false,
                        Similarity: 0,
                        SimilarityPercent: 0,
                        Threshold: test.Threshold,
                        ThresholdPercent: test.Threshold,
                        IsHidden: test.IsHidden,
                        ReferenceUrl: canViewReferenceImages ? test.ExpectedImageUrl : null,
                        SubmittedUrl: null,
                        Stderr: renderRaw,
                        Analyzer: null));
                    continue;
                }

                using var renderDoc = JsonDocument.Parse(renderRaw);
                var pngBase64 = renderDoc.RootElement.TryGetProperty("pngBase64", out var p) ? p.GetString() : null;
                var stdout = renderDoc.RootElement.TryGetProperty("stdout", out var so) ? so.GetString() ?? string.Empty : string.Empty;
                var stderr = renderDoc.RootElement.TryGetProperty("stderr", out var se) ? se.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(pngBase64))
                {
                    results.Add(new ImageCaseResult(i + 1, test.Name, test.Input, test.IsHidden ? null : test.ExpectedOutput, stdout, StdoutMatches(stdout, test.ExpectedOutput), false, false, 0, 0, test.Threshold, test.Threshold, test.IsHidden, canViewReferenceImages ? test.ExpectedImageUrl : null, null, stderr + "\nRunner завершился без PNG-изображения.", null));
                    continue;
                }

                var expected = await LoadExpectedImageAsync(test, clients, cfg, CancellationToken.None);
                var expectedBytes = expected.Bytes;
                var actualBytes = Convert.FromBase64String(pngBase64);
                var actualUrl = $"data:image/png;base64,{pngBase64}";
                if (submit && currentUserId.HasValue)
                {
                    var uploadedActual = await UploadImageBytesToFilesApiAsync(clients, cfg, actualBytes, $"case-{i + 1}-actual.png", "image/png", $"image-tests/submissions/{assignmentId:N}/{currentUserId.Value:N}", CancellationToken.None);
                    actualUrl = uploadedActual.PrivateUrl ?? PrivateFileUrl(uploadedActual.Key);
                }
                var threshold = System.Math.Clamp(test.Threshold / 100.0, 0.0, 1.0);
                using var mp = new MultipartFormDataContent();
                var expectedPart = new ByteArrayContent(expectedBytes);
                if (!string.IsNullOrWhiteSpace(expected.ContentType)) expectedPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(expected.ContentType);
                mp.Add(expectedPart, "expected", test.ExpectedImageFileName ?? expected.FileName ?? "expected.png");
                var actualPart = new ByteArrayContent(actualBytes);
                actualPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                mp.Add(actualPart, "actual", "actual.png");
                var compared = await client.PostAsync($"http://image-analyzer:8080/compare?threshold={threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}", mp);
                var compareRaw = await compared.Content.ReadAsStringAsync();
                if (!compared.IsSuccessStatusCode)
                {
                    results.Add(new ImageCaseResult(i + 1, test.Name, test.Input, test.IsHidden ? null : test.ExpectedOutput, stdout, StdoutMatches(stdout, test.ExpectedOutput), false, false, 0, 0, test.Threshold, test.Threshold, test.IsHidden, canViewReferenceImages ? test.ExpectedImageUrl : null, actualUrl, stderr + "\n" + compareRaw, null));
                    continue;
                }
                var analyzer = JsonSerializer.Deserialize<JsonElement>(compareRaw);
                var combined = analyzer.TryGetProperty("combined_similarity", out var c) && c.TryGetDouble(out var cv) ? cv : 0.0;
                var imagePassed = analyzer.TryGetProperty("passed", out var pass) && pass.ValueKind == JsonValueKind.True;
                var similarityPercent = System.Math.Round(combined * 100, 2);
                var stdoutPassed = StdoutMatches(stdout, test.ExpectedOutput);
                results.Add(new ImageCaseResult(
                    Index: i + 1,
                    Name: test.Name,
                    Input: test.Input,
                    ExpectedOutput: test.IsHidden ? null : test.ExpectedOutput,
                    ActualOutput: stdout,
                    StdoutPassed: stdoutPassed,
                    ImagePassed: imagePassed,
                    Passed: stdoutPassed && imagePassed,
                    Similarity: similarityPercent,
                    SimilarityPercent: similarityPercent,
                    Threshold: test.Threshold,
                    ThresholdPercent: test.Threshold,
                    IsHidden: test.IsHidden,
                    ReferenceUrl: canViewReferenceImages ? test.ExpectedImageUrl : null,
                    SubmittedUrl: actualUrl,
                    Stderr: stderr,
                    Analyzer: analyzer));
            }

            var passed = results.Count > 0 && results.All(x => x.Passed);
            var passedCount = results.Count(x => x.Passed);
            var similarityPercentInt = results.Count == 0 ? 0 : (int)System.Math.Round(results.Average(x => x.SimilarityPercent));
            var referenceUrl = canViewReferenceImages ? results.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ReferenceUrl))?.ReferenceUrl ?? ReferenceUrl(root) : null;
            var submittedUrl = results.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SubmittedUrl))?.SubmittedUrl;
            var stdoutJoined = string.Join("\n---\n", results.Select(x => x.ActualOutput).Where(x => !string.IsNullOrWhiteSpace(x)));
            var stderrJoined = string.Join("\n---\n", results.Select(x => x.Stderr).Where(x => !string.IsNullOrWhiteSpace(x)));
            var savedSolutionId = (Guid?)null;

            var resultPayload = new
            {
                assignmentId,
                submitted = submit,
                passed,
                passedCount,
                total = results.Count,
                similarity = similarityPercentInt,
                similarityPercent = similarityPercentInt,
                threshold = results.Count == 0 ? 90 : results.Min(x => x.ThresholdPercent),
                thresholdPercent = results.Count == 0 ? 90 : results.Min(x => x.ThresholdPercent),
                stdout = stdoutJoined,
                stderr = stderrJoined,
                referenceUrl,
                submittedUrl,
                cases = results
            };

            if (submit && currentUserId.HasValue)
            {
                savedSolutionId = await SaveImageSolutionAsync(
                    assignmentId,
                    currentUserId.Value,
                    lang,
                    request.Code ?? string.Empty,
                    similarityPercentInt,
                    passed,
                    JsonSerializer.SerializeToElement(resultPayload, JsonOptions()),
                    cfg,
                    clients);
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                id = savedSolutionId,
                solutionId = savedSolutionId,
                assignmentId,
                submitted = submit,
                passed,
                passedCount,
                total = results.Count,
                similarity = similarityPercentInt,
                similarityPercent = similarityPercentInt,
                threshold = results.Count == 0 ? 90 : results.Min(x => x.ThresholdPercent),
                thresholdPercent = results.Count == 0 ? 90 : results.Min(x => x.ThresholdPercent),
                stdout = stdoutJoined,
                stderr = stderrJoined,
                referenceUrl,
                submittedUrl,
                cases = results
            });
        }
        catch (Exception ex)
        {
            if (context is not null && currentUserId.HasValue)
            {
                await RefundTaskEnergyAsync(context, cfg, clients, currentUserId.Value, submit ? "image-submit-code-failed" : "image-compare-code-failed", CancellationToken.None);
            }
            return Problem(503, "IMAGE_CODE_COMPARE_FAILED", submit ? "image-test.submit-code" : "image-test.compare-code", "Не удалось выполнить image-code pipeline.", ex.Message);
        }
    }

}
