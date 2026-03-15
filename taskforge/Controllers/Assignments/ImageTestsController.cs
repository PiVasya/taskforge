using System.Text;
using Microsoft.AspNetCore.Authorization;
using taskforge.Constants;
using taskforge.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Helpers;
using taskforge.Services.Files;
using taskforge.Services.ImageRunners;
using taskforge.Services.ImageTests;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Assignments;

[ApiController]
[Route("api/assignments/{assignmentId:guid}/image-tests")]
// Backward compatibility (older front builds used singular "image-test")
[Route("api/assignments/{assignmentId:guid}/image-test")]
[Authorize]
public sealed class ImageTestsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IFileStorageService _files;
    private readonly IImageSimilarityService _similarity;
    private readonly ICurrentUserService _currentUser;
    private readonly ICourseAccessService _access;
    private readonly IImageRunnerClient _runner;
    private readonly ILogger<ImageTestsController> _log;

    public ImageTestsController(
        ApplicationDbContext db,
        IFileStorageService files,
        IImageSimilarityService similarity,
        ICurrentUserService currentUser,
        ICourseAccessService access,
        IImageRunnerClient runner,
        ILogger<ImageTestsController> log)
    {
        _db = db;
        _files = files;
        _similarity = similarity;
        _currentUser = currentUser;
        _access = access;
        _runner = runner;
        _log = log;
    }

    public sealed record UploadReferenceResponse(string Key, double Threshold, string ReferenceUrl);

    /// <summary>
    /// Загрузить/заменить эталон для image-test.
    /// Клиент (редактор задания) отправляет multipart/form-data: file, threshold (0..100).
    /// </summary>
    [HttpPost("reference")]
    public async Task<ActionResult<UploadReferenceResponse>> UploadReference(
        [FromRoute] Guid assignmentId,
        [FromForm] IFormFile file,
        [FromForm] double threshold,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("File is empty");
        if (threshold < 0) threshold = 0;
        if (threshold > 100) threshold = 100;

        var uid = _currentUser.GetUserId();
        var role = _currentUser.GetRole();

        var a = await _db.TaskAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (a is null) return NotFound();

        if (!await _access.CanEditCourseAsync(uid, role, a.CourseId))
            return Forbid();

        if (a.Type != TaskAssignmentTypes.ImageTest)
            return BadRequest("Assignment is not image-test");

        // Сохраняем эталон в storage и пишем ключ прямо в задание
        var key = await _files.UploadImageAsync(file, $"image-tests/reference/{assignmentId}", ct);
        a.ImageTestReferenceKey = key;
        a.ImageTestSimilarityThreshold = threshold;
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var referenceUrl = $"/api/private-files/{Uri.EscapeDataString(key)}";
        return Ok(new UploadReferenceResponse(Key: key, Threshold: threshold, ReferenceUrl: referenceUrl));
    }

    // Added optional MimeType property so the client can specify the uploaded image mime type.
    public sealed record CompareUploadedImageRequest(string SubmittedImageBase64)
    {
        public string? MimeType { get; init; }
    }

    public sealed record CompareCodeRequest(string Language, string Code, bool Debug = false);

    /// <summary>
    /// "Пробный запуск" рисовалки: просто рендер без сравнения.
    /// По желанию можно включить сравнение с эталоном.
    /// </summary>
    public sealed record RunCodeRequest(string Language, string Code, bool Debug = false, bool CompareWithReference = false);

    public sealed record ImageTestRunResponse(
        bool Ok,
        string? RenderedKey,
        string? RenderedUrl,
        string Stdout,
        string Stderr,
        string? RunnerError,
        double? SimilarityPercent,
        double? ThresholdPercent,
        bool? Passed,
        string? ReferenceUrl,
        Guid? SolutionId = null);

    public sealed record ImageTestCompareResponse(
        bool Ok,
        double SimilarityPercent,
        double ThresholdPercent,
        bool Passed,
        string ReferenceKey,
        string? SubmittedKey,
        string ReferenceUrl,
        string? SubmittedUrl,
        string Stdout,
        string Stderr,
        string? RunnerError,
        Guid? SolutionId = null);

    private async Task<Guid> SaveImageSolutionAsync(
        Guid userId,
        TaskAssignment a,
        string kind,
        bool isTrial,
        string? language,
        string? submittedCode,
        string? submittedKey,
        double? similarityPercent,
        double? thresholdPercent,
        bool? passed,
        string stdout,
        string stderr,
        string? runnerError,
        CancellationToken ct)
    {
        var s = new UserImageTaskSolution
        {
            UserId = userId,
            TaskAssignmentId = a.Id,
            Kind = kind,
            IsTrial = isTrial,
            Language = language,
            SubmittedCode = submittedCode,
            ReferenceKey = a.ImageTestReferenceKey,
            SubmittedKey = submittedKey,
            SimilarityPercent = similarityPercent,
            ThresholdPercent = thresholdPercent,
            Passed = passed,
            Stdout = stdout ?? string.Empty,
            Stderr = stderr ?? string.Empty,
            RunnerError = runnerError,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.UserImageTaskSolutions.Add(s);
        await _db.SaveChangesAsync(ct);
        return s.Id;
    }

    /// <summary>
    /// Compare a user-uploaded image (multipart/form-data) against the reference image.
    /// This endpoint accepts an uploaded file and returns similarity information.
    /// It matches the old front-end call /image-test/compare.
    /// </summary>
    [HttpPost("compare")]
    [RequireQuota(QuotaBuckets.Tasks)]
    public async Task<ActionResult<ImageTestCompareResponse>> Compare(
        [FromRoute] Guid assignmentId,
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        var trace = HttpContext.TraceIdentifier;
        DebugConsole.Log("ImageTests", $"Compare(multipart) start trace={trace} assignmentId={assignmentId} fileLength={file?.Length ?? 0}");
        _log.LogInformation("Compare (multipart) start trace={Trace} assignmentId={AssignmentId} fileLength={Len}", trace, assignmentId, file?.Length ?? 0);

        TaskAssignment a;
        try
        {
            a = await GetViewableImageAssignmentAsync(assignmentId, ct) ?? throw new KeyNotFoundException();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        if (a is null) return NotFound();
        if (a.Type != TaskAssignmentTypes.ImageTest) return BadRequest("Assignment is not image-test");
        if (string.IsNullOrWhiteSpace(a.ImageTestReferenceKey)) return BadRequest("Reference image is not configured");
        if (file is null || file.Length == 0) return BadRequest("File is empty");

        var userId = _currentUser.GetUserId();

        // Read uploaded bytes
        byte[] submittedBytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            submittedBytes = ms.ToArray();
        }

        DebugConsole.Log("ImageTests", $"Uploaded bytes read trace={trace} submittedBytes={submittedBytes.Length}");

        // Determine mime type; fallback to file.ContentType or default to image/png
        var mime = !string.IsNullOrWhiteSpace(file.ContentType) ? file.ContentType : "image/png";

        // Upload submitted image
        var submittedKey = await _files.UploadBytesAsync(submittedBytes, mime, $"image-tests/submissions/{userId}/{assignmentId}", ".png", ct);
        DebugConsole.Log("ImageTests", $"Uploaded submitted image trace={trace} key={submittedKey} mime={mime}");

        // Compare with reference
        var (refStreamRaw, _) = await _files.GetAsync(a.ImageTestReferenceKey, ct);
        await using var refStream = refStreamRaw;
        await using var subStream = new MemoryStream(submittedBytes);

        DebugConsole.Log("ImageTests", $"Fetched reference trace={trace} referenceKey={a.ImageTestReferenceKey}");

        // Threshold can be stored either as 0..1 or 0..100 (legacy). Normalize to percent.
        var thresholdPercent = a.ImageTestSimilarityThreshold ?? 70.0;
        if (thresholdPercent <= 1.0) thresholdPercent *= 100.0;

        double similarityPercent;
        try
        {
            similarityPercent = await _similarity.GetSimilarityPercentAsync(refStream, subStream, ct);
        }
        catch (ImageAnalyzerUnavailableException)
        {
            var submittedUrl503 = $"/api/private-files/{Uri.EscapeDataString(submittedKey)}";

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: Math.Round(thresholdPercent, 1),
                Passed: false,
                ReferenceKey: string.Empty,
                SubmittedKey: submittedKey,
                ReferenceUrl: null,
                SubmittedUrl: submittedUrl503,
                Stdout: string.Empty,
                Stderr: string.Empty,
                RunnerError: "Сервис сравнения изображений временно недоступен. Попробуйте позже."));
        }

        var passed = similarityPercent >= thresholdPercent;
        var canEdit = await CanEditAssignmentAsync(a, ct);

        var referenceUrl = canEdit ? $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}" : null;
        var submittedUrl = $"/api/private-files/{Uri.EscapeDataString(submittedKey)}";

        var solutionId = await SaveImageSolutionAsync(
            userId,
            a,
            kind: "upload",
            isTrial: false,
            language: null,
            submittedCode: null,
            submittedKey: submittedKey,
            similarityPercent: similarityPercent,
            thresholdPercent: thresholdPercent,
            passed: passed,
            stdout: string.Empty,
            stderr: string.Empty,
            runnerError: null,
            ct);

        return Ok(new ImageTestCompareResponse(
            Ok: true,
            SimilarityPercent: Math.Round(similarityPercent, 1),
            ThresholdPercent: Math.Round(thresholdPercent, 1),
            Passed: passed,
            ReferenceKey: canEdit ? a.ImageTestReferenceKey! : string.Empty,
            SubmittedKey: submittedKey,
            ReferenceUrl: referenceUrl,
            SubmittedUrl: submittedUrl,
            Stdout: string.Empty,
            Stderr: string.Empty,
            RunnerError: null,
            SolutionId: solutionId));
    }

    [HttpPost("compare-upload")]
    [RequireQuota(QuotaBuckets.Tasks)]
    public async Task<ActionResult<ImageTestCompareResponse>> CompareUpload([FromRoute] Guid assignmentId, [FromBody] CompareUploadedImageRequest req, CancellationToken ct)
    {
        var trace = HttpContext.TraceIdentifier;
        // Use req.MimeType for logging purposes; this may be null when not supplied.
        _log.LogInformation("CompareUpload start trace={Trace} assignmentId={AssignmentId} mime={MimeType} base64Len={Len}", trace, assignmentId, req.MimeType, req.SubmittedImageBase64?.Length ?? 0);
        TaskAssignment a;
        try
        {
            a = await GetViewableImageAssignmentAsync(assignmentId, ct) ?? throw new KeyNotFoundException();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        if (a is null) return NotFound();
        if (a.Type != TaskAssignmentTypes.ImageTest) return BadRequest("Assignment is not image-test");
        if (string.IsNullOrWhiteSpace(a.ImageTestReferenceKey)) return BadRequest("Reference image is not configured");

        var userId = _currentUser.GetUserId();

        // 1) Decode submitted image from base64 (dataURL or plain base64)
        byte[] submittedBytes;
        try
        {
            submittedBytes = Base64Helper.DecodeDataUrlOrBase64(req.SubmittedImageBase64);
        }
        catch
        {
            return BadRequest("Invalid base64 image");
        }

        // 2) Upload submitted image
        // Default to "image/png" if no mime type is provided by the client.
        var mimeType = string.IsNullOrWhiteSpace(req.MimeType) ? "image/png" : req.MimeType!;
        var submittedKey = await _files.UploadBytesAsync(submittedBytes, mimeType, $"image-tests/submissions/{userId}/{assignmentId}", ".png", ct);

        // 3) Compare with reference
        var (refStreamRaw, _) = await _files.GetAsync(a.ImageTestReferenceKey, ct);
        await using var refStream = refStreamRaw;
        await using var subStream = new MemoryStream(submittedBytes);

        // Threshold can be stored either as 0..1 or 0..100 (legacy). Normalize to percent.
        var thresholdPercent = a.ImageTestSimilarityThreshold ?? 70.0;
        if (thresholdPercent <= 1.0) thresholdPercent *= 100.0;

        double similarityPercent;
        try
        {
            similarityPercent = await _similarity.GetSimilarityPercentAsync(refStream, subStream, ct);
        }
        catch (ImageAnalyzerUnavailableException)
        {
            var submittedUrl503 = $"/api/private-files/{Uri.EscapeDataString(submittedKey)}";

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: Math.Round(thresholdPercent, 1),
                Passed: false,
                ReferenceKey: string.Empty,
                SubmittedKey: submittedKey,
                ReferenceUrl: null,
                SubmittedUrl: submittedUrl503,
                Stdout: "",
                Stderr: "",
                RunnerError: "Сервис сравнения изображений временно недоступен. Попробуйте позже."));
        }

        var passed = similarityPercent >= thresholdPercent;
        var canEdit = await CanEditAssignmentAsync(a, ct);

        var referenceUrl = canEdit ? $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}" : null;
        var submittedUrl = $"/api/private-files/{Uri.EscapeDataString(submittedKey)}";

        var solutionId = await SaveImageSolutionAsync(
            userId,
            a,
            kind: "upload",
            isTrial: false,
            language: null,
            submittedCode: null,
            submittedKey: submittedKey,
            similarityPercent: similarityPercent,
            thresholdPercent: thresholdPercent,
            passed: passed,
            stdout: string.Empty,
            stderr: string.Empty,
            runnerError: null,
            ct);

        return Ok(new ImageTestCompareResponse(
            Ok: true,
            SimilarityPercent: Math.Round(similarityPercent, 1),
            ThresholdPercent: Math.Round(thresholdPercent, 1),
            Passed: passed,
            ReferenceKey: canEdit ? a.ImageTestReferenceKey : string.Empty,
            SubmittedKey: submittedKey,
            ReferenceUrl: referenceUrl,
            SubmittedUrl: submittedUrl,
            Stdout: "",
            Stderr: "",
            RunnerError: null,
            SolutionId: solutionId));
    }

    /// <summary>
    /// Пробный запуск рисовалки: прогнать код в runner-е и вернуть полученную картинку.
    /// Сравнение с эталоном отключено по умолчанию, но его можно включить флагом CompareWithReference.
    /// </summary>
    [HttpPost("run-code")]
    [RequireQuota(QuotaBuckets.Tasks)]
    public async Task<ActionResult<ImageTestRunResponse>> RunCode([FromRoute] Guid assignmentId, [FromBody] RunCodeRequest req, CancellationToken ct)
    {
        var trace = HttpContext.TraceIdentifier;
        var isAdmin = User.IsInRole("Admin");
        var effectiveDebug = isAdmin && req.Debug;
        _log.LogInformation("RunCode start trace={Trace} assignmentId={AssignmentId} lang={Lang} codeLen={Len} debug={Debug} compare={Compare}",
            trace, assignmentId, req.Language, req.Code?.Length ?? 0, effectiveDebug, req.CompareWithReference);

        TaskAssignment a;
        try
        {
            a = await GetViewableImageAssignmentAsync(assignmentId, ct) ?? throw new KeyNotFoundException();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        if (a is null) return NotFound();
        if (a.Type != TaskAssignmentTypes.ImageTest) return BadRequest("Assignment is not image-test");
        if (string.IsNullOrWhiteSpace(req.Code)) return BadRequest("Code is empty");

                var allowedLangs = GetAllowedImageLangs(a);

        var lang = NormalizeLang(req.Language ?? string.Empty);
        if (!allowedLangs.Contains(lang))
            return BadRequest($"Language is not allowed for this task. Allowed: {string.Join(", ", allowedLangs)}");
// Guardrail: users sometimes paste Pascal into a Python editor (or vice versa).
        // That leads to confusing errors like Python SyntaxError for Pascal comments.
        // We auto-correct the language for image-tests based on simple heuristics.
        // (Better UX than returning a misleading runner error.)
        var code = req.Code ?? string.Empty;
        if (lang == "python" && LooksLikePascal(code))
        {
            _log.LogWarning("RunCode language auto-correct: python -> pascal (trace={Trace} assignmentId={AssignmentId})", trace, assignmentId);
            lang = "pascal";
        }
        else if (lang == "pascal" && LooksLikePython(code))
        {
            _log.LogWarning("RunCode language auto-correct: pascal -> python (trace={Trace} assignmentId={AssignmentId})", trace, assignmentId);
            lang = "python";
        }

        var userId = _currentUser.GetUserId();

        ImageRunnerDebugResult? debug = null;
        byte[]? png;
        string stdout = "";
        string stderr = "";
        string? runnerErr = null;

        if (effectiveDebug && lang == "python")
        {
            try
            {
                debug = await _runner.RenderDebugAsync(lang, code, ct);
            }
            catch (TaskCanceledException)
            {
                return Ok(new ImageTestRunResponse(
                    Ok: false,
                    RenderedKey: null,
                    RenderedUrl: null,
                    Stdout: "",
                    Stderr: "",
                    RunnerError: "Image runner timed out",
                    SimilarityPercent: null,
                    ThresholdPercent: null,
                    Passed: null,
                    ReferenceUrl: null));
            }
            catch (taskforge.Services.ImageRunners.ImageRunnerHttpException ex)
            {
                return Ok(new ImageTestRunResponse(
                    Ok: false,
                    RenderedKey: null,
                    RenderedUrl: null,
                    Stdout: "",
                    Stderr: "",
                    RunnerError: $"Image runner failed ({(int)ex.StatusCode})",
                    SimilarityPercent: null,
                    ThresholdPercent: null,
                    Passed: null,
                    ReferenceUrl: null));
            }
            catch (HttpRequestException ex)
            {
                return Ok(new ImageTestRunResponse(
                    Ok: false,
                    RenderedKey: null,
                    RenderedUrl: null,
                    Stdout: "",
                    Stderr: "",
                    RunnerError: $"Image runner request failed: {ex.Message}",
                    SimilarityPercent: null,
                    ThresholdPercent: null,
                    Passed: null,
                    ReferenceUrl: null));
            }
            // stdout/stderr никогда не уходят на фронт.
            stdout = debug.Stdout;
            stderr = debug.Stderr;
            runnerErr = debug.Error;
            png = debug.PngBytes;

            if (!debug.Ok)
            {
                return Ok(new ImageTestRunResponse(
                    Ok: false,
                    RenderedKey: null,
                    RenderedUrl: null,
                    Stdout: "",
                    Stderr: "",
                    RunnerError: runnerErr,
                    SimilarityPercent: null,
                    ThresholdPercent: null,
                    Passed: null,
                    ReferenceUrl: null));
            }
        }
        else
        {
            try
            {
                png = await _runner.RenderAsync(lang, req.Code, ct);
            }
            catch (TaskCanceledException)
            {
                return Ok(new ImageTestRunResponse(
                    Ok: false,
                    RenderedKey: null,
                    RenderedUrl: null,
                    Stdout: "",
                    Stderr: "",
                    RunnerError: "Image runner timed out",
                    SimilarityPercent: null,
                    ThresholdPercent: null,
                    Passed: null,
                    ReferenceUrl: null));
            }
            catch (taskforge.Services.ImageRunners.ImageRunnerHttpException ex)
            {
                return Ok(new ImageTestRunResponse(
                    Ok: false,
                    RenderedKey: null,
                    RenderedUrl: null,
                    Stdout: "",
                    Stderr: "",
                    RunnerError: $"Image runner failed ({(int)ex.StatusCode})",
                    SimilarityPercent: null,
                    ThresholdPercent: null,
                    Passed: null,
                    ReferenceUrl: null));
            }
            catch (HttpRequestException ex)
            {
                return Ok(new ImageTestRunResponse(
                    Ok: false,
                    RenderedKey: null,
                    RenderedUrl: null,
                    Stdout: "",
                    Stderr: "",
                    RunnerError: $"Image runner request failed: {ex.Message}",
                    SimilarityPercent: null,
                    ThresholdPercent: null,
                    Passed: null,
                    ReferenceUrl: null));
            }
        }

        if (png is null || png.Length == 0)
        {
            return Ok(new ImageTestRunResponse(
                Ok: false,
                RenderedKey: null,
                RenderedUrl: null,
                Stdout: "",
                Stderr: "",
                RunnerError: runnerErr ?? "Empty image returned",
                SimilarityPercent: null,
                ThresholdPercent: null,
                Passed: null,
                ReferenceUrl: null));
        }

        // Upload rendered image
        var renderedKey = await _files.UploadBytesAsync(png, "image/png", $"image-tests/previews/{userId}/{assignmentId}", ".png", ct);
        var renderedUrl = $"/api/private-files/{Uri.EscapeDataString(renderedKey)}";

        // Пробник: только рендер, без сравнения.
        var solutionId = await SaveImageSolutionAsync(
            userId,
            a,
            kind: "code",
            isTrial: true,
            language: lang,
            submittedCode: req.Code,
            submittedKey: renderedKey,
            similarityPercent: null,
            thresholdPercent: null,
            passed: null,
            stdout: string.Empty,
            stderr: string.Empty,
            runnerError: runnerErr,
            ct);

        return Ok(new ImageTestRunResponse(
            Ok: true,
            RenderedKey: renderedKey,
            RenderedUrl: renderedUrl,
            Stdout: "",
            Stderr: "",
            RunnerError: runnerErr,
            SimilarityPercent: null,
            ThresholdPercent: null,
            Passed: null,
            ReferenceUrl: null,
            SolutionId: solutionId));
    }

    /// <summary>
    /// Главная штука: пользователь присылает код (Python/Pascal),
    /// backend рендерит картинку внутри контейнера и сравнивает с эталоном.
    /// Пользователь не загружает изображения вручную.
    /// </summary>
    [HttpPost("compare-code")]
    [RequireQuota(QuotaBuckets.Tasks)]
    public async Task<ActionResult<ImageTestCompareResponse>> CompareCode([FromRoute] Guid assignmentId, [FromBody] CompareCodeRequest req, CancellationToken ct)
    {
        var trace = HttpContext.TraceIdentifier;
        var isAdmin = User.IsInRole("Admin");
        var effectiveDebug = isAdmin && req.Debug;
        DebugConsole.Log("ImageTests", $"CompareCode start trace={trace} assignmentId={assignmentId} lang={req.Language} codeLen={req.Code?.Length ?? 0} debug={req.Debug}");
        _log.LogInformation("CompareCode start trace={Trace} assignmentId={AssignmentId} lang={Lang} codeLen={Len}", trace, assignmentId, req.Language, req.Code?.Length ?? 0);
        TaskAssignment a;
        try
        {
            a = await GetViewableImageAssignmentAsync(assignmentId, ct) ?? throw new KeyNotFoundException();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        if (a is null) return NotFound();
        if (a.Type != TaskAssignmentTypes.ImageTest) return BadRequest("Assignment is not image-test");
        if (string.IsNullOrWhiteSpace(a.ImageTestReferenceKey)) return BadRequest("Reference image is not configured");
        if (string.IsNullOrWhiteSpace(req.Code)) return BadRequest("Code is empty");

        var canEdit = await CanEditAssignmentAsync(a, ct);
        var referenceUrlForUser = canEdit ? $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}" : null;

        var allowedLangs = GetAllowedImageLangs(a);

        var lang = NormalizeLang(req.Language ?? string.Empty);
        if (!allowedLangs.Contains(lang))
            return BadRequest($"Language is not allowed for this task. Allowed: {string.Join(", ", allowedLangs)}");
var userId = _currentUser.GetUserId();
        var codeLen = Encoding.UTF8.GetByteCount(req.Code);

        _log.LogInformation("[ImageTest] compare-code start: assignment={AssignmentId} user={UserId} lang={Lang} bytes={Bytes}", assignmentId, userId, lang, codeLen);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        ImageRunnerDebugResult? debug = null;
        byte[]? png;
        string stdout = "";
        string stderr = "";
        string? runnerErr = null;

        // normalize threshold (support old configs: 0..1 as fraction)
        var thresholdPercent = a.ImageTestSimilarityThreshold ?? 90.0;
        if (thresholdPercent <= 1.0) thresholdPercent *= 100.0;

        if (effectiveDebug && lang == "python")
        {
            debug = await _runner.RenderDebugAsync(lang, req.Code, ct);
            stdout = debug.Stdout;
            stderr = debug.Stderr;
            runnerErr = debug.Error;
            png = debug.PngBytes;

            if (!debug.Ok)
            {
                _log.LogWarning("[ImageTest] runner debug failed: assignment={AssignmentId} user={UserId} err={Err}", assignmentId, userId, runnerErr);
                return Ok(new ImageTestCompareResponse(
                    Ok: false,
                    SimilarityPercent: 0,
                    ThresholdPercent: Math.Round(thresholdPercent, 1),
                    Passed: false,
                    ReferenceKey: canEdit ? a.ImageTestReferenceKey : string.Empty,
                    SubmittedKey: null,
                    ReferenceUrl: referenceUrlForUser,
                    SubmittedUrl: null,
                    Stdout: "",
                    Stderr: "",
                    RunnerError: runnerErr));
            }
        }
        else
        {
            try
            {
                png = await _runner.RenderAsync(lang, req.Code, ct);
            }
            catch (TaskCanceledException)
            {
                runnerErr = "Render timeout";
                return Ok(new ImageTestCompareResponse(
                    Ok: false,
                    SimilarityPercent: 0,
                    ThresholdPercent: Math.Round(thresholdPercent, 1),
                    Passed: false,
                    ReferenceKey: canEdit ? a.ImageTestReferenceKey : string.Empty,
                    SubmittedKey: null,
                    ReferenceUrl: referenceUrlForUser,
                    SubmittedUrl: null,
                    Stdout: "",
                    Stderr: "",
                    RunnerError: runnerErr));
            }
            catch (taskforge.Services.ImageRunners.ImageRunnerHttpException ex)
            {
                runnerErr = $"Image runner failed ({(int)ex.StatusCode})";
                return Ok(new ImageTestCompareResponse(
                    Ok: false,
                    SimilarityPercent: 0,
                    ThresholdPercent: Math.Round(thresholdPercent, 1),
                    Passed: false,
                    ReferenceKey: canEdit ? a.ImageTestReferenceKey : string.Empty,
                    SubmittedKey: null,
                    ReferenceUrl: referenceUrlForUser,
                    SubmittedUrl: null,
                    Stdout: "",
                    Stderr: "",
                    RunnerError: runnerErr));
            }
            catch (HttpRequestException ex)
            {
                runnerErr = $"Runner HTTP error: {ex.Message}";
                return Ok(new ImageTestCompareResponse(
                    Ok: false,
                    SimilarityPercent: 0,
                    ThresholdPercent: Math.Round(thresholdPercent, 1),
                    Passed: false,
                    ReferenceKey: canEdit ? a.ImageTestReferenceKey : string.Empty,
                    SubmittedKey: null,
                    ReferenceUrl: referenceUrlForUser,
                    SubmittedUrl: null,
                    Stdout: "",
                    Stderr: "",
                    RunnerError: runnerErr));
            }
        }

        if (png is null || png.Length == 0)
        {
            _log.LogWarning("[ImageTest] runner returned empty image: assignment={AssignmentId} user={UserId} lang={Lang}", assignmentId, userId, lang);
            return Ok(new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: Math.Round(thresholdPercent, 1),
                Passed: false,
                ReferenceKey: canEdit ? a.ImageTestReferenceKey : string.Empty,
                SubmittedKey: null,
                ReferenceUrl: referenceUrlForUser,
                SubmittedUrl: null,
                Stdout: "",
                Stderr: "",
                RunnerError: runnerErr ?? "Empty image returned"));
        }

        // Upload submitted image
        var submittedKey = await _files.UploadBytesAsync(png, "image/png", $"image-tests/submissions/{userId}/{assignmentId}", ".png", ct);

        // Compare with reference
        var (refStream, _) = await _files.GetAsync(a.ImageTestReferenceKey, ct);
        await using var refS = refStream;
        await using var subStream = new MemoryStream(png);

        DebugConsole.Log("ImageTests", $"[ImageTest] similarity start: assignment={assignmentId} user={userId} refKey={a.ImageTestReferenceKey} subKey={submittedKey} threshold={thresholdPercent:0.0}% refBytes={(refS.CanSeek ? refS.Length : -1)} subBytes={png.Length}");

        double similarityPercent;
        bool passed;
        try
        {
            similarityPercent = await _similarity.GetSimilarityPercentAsync(refS, subStream, ct);
            passed = similarityPercent >= thresholdPercent;
        }
        catch (ImageAnalyzerUnavailableException)
        {
            var solutionId503 = await SaveImageSolutionAsync(
                userId,
                a,
                kind: "code",
                isTrial: false,
                language: lang,
                submittedCode: req.Code,
                submittedKey: submittedKey,
                similarityPercent: null,
                thresholdPercent: thresholdPercent,
                passed: null,
                stdout: string.Empty,
                stderr: string.Empty,
                runnerError: "Сервис сравнения изображений временно недоступен. Попробуйте позже.",
                ct);

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: Math.Round(thresholdPercent, 1),
                Passed: false,
                ReferenceKey: canEdit ? a.ImageTestReferenceKey : string.Empty,
                SubmittedKey: submittedKey,
                ReferenceUrl: referenceUrlForUser,
                SubmittedUrl: $"/api/private-files/{Uri.EscapeDataString(submittedKey)}",
                Stdout: "",
                Stderr: "",
                RunnerError: "Сервис сравнения изображений временно недоступен. Попробуйте позже.",
                SolutionId: solutionId503));
        }
        catch (Exception ex)
        {
            // IMPORTANT: image-test сравнение не должно валить весь запрос (и тем более весь контейнер)
            // Логи ниже максимально подробные, чтобы проще дебажить native/OpenCV проблемы.
            _log.LogError(ex, "[ImageTest] similarity failed: assignment={AssignmentId} user={UserId} lang={Lang} refKey={RefKey} subKey={SubKey}",
                assignmentId, userId, lang, a.ImageTestReferenceKey, submittedKey);
            DebugConsole.Log("ImageTests", $"[ImageTest] similarity FAILED: {ex.GetType().Name}: {ex.Message}");

            var solutionIdFail = await SaveImageSolutionAsync(
                userId,
                a,
                kind: "code",
                isTrial: false,
                language: lang,
                submittedCode: req.Code,
                submittedKey: submittedKey,
                similarityPercent: null,
                thresholdPercent: thresholdPercent,
                passed: null,
                stdout: string.Empty,
                stderr: string.Empty,
                runnerError: runnerErr ?? $"Image similarity failed: {ex.GetType().Name}: {ex.Message}",
                ct);

            return Ok(new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: Math.Round(thresholdPercent, 1),
                Passed: false,
                ReferenceKey: canEdit ? a.ImageTestReferenceKey : string.Empty,
                SubmittedKey: submittedKey,
                ReferenceUrl: referenceUrlForUser,
                SubmittedUrl: $"/api/private-files/{Uri.EscapeDataString(submittedKey)}",
                Stdout: "",
                Stderr: "",
                RunnerError: runnerErr ?? $"Image similarity failed: {ex.GetType().Name}: {ex.Message}",
                SolutionId: solutionIdFail));
        }

        sw.Stop();
        _log.LogInformation("[ImageTest] compare-code done: assignment={AssignmentId} user={UserId} lang={Lang} similarity={Similarity:0.000} passed={Passed} ms={Ms}",
            assignmentId, userId, lang, similarityPercent, passed, sw.ElapsedMilliseconds);

        var solutionId = await SaveImageSolutionAsync(
            userId,
            a,
            kind: "code",
            isTrial: false,
            language: lang,
            submittedCode: req.Code,
            submittedKey: submittedKey,
            similarityPercent: similarityPercent,
            thresholdPercent: thresholdPercent,
            passed: passed,
            stdout: string.Empty,
            stderr: string.Empty,
            runnerError: runnerErr,
            ct);

        var response = new ImageTestCompareResponse(
            Ok: true,
            SimilarityPercent: Math.Round(similarityPercent, 1),
            ThresholdPercent: Math.Round(thresholdPercent, 1),
            Passed: passed,
            ReferenceKey: canEdit ? a.ImageTestReferenceKey : string.Empty,
            SubmittedKey: submittedKey,
            ReferenceUrl: referenceUrlForUser,
            SubmittedUrl: $"/api/private-files/{Uri.EscapeDataString(submittedKey)}",
            Stdout: "",
            Stderr: "",
            RunnerError: runnerErr,
            SolutionId: solutionId);

        return Ok(response);
    }


    private static readonly HashSet<string> SupportedImageRunnerLangs = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "pascal"
    };

    private static string NormalizeLang(string x)
    {
        var s = (x ?? string.Empty).Trim().ToLowerInvariant();
        if (s == "py" || s == "python") return "python";
        if (s == "pas" || s == "pascal" || s == "pascalabc" || s == "pascalabcnet") return "pascal";
        return s;
    }

    private static HashSet<string> GetAllowedImageLangs(taskforge.Data.Models.Entities.TaskAssignment a)
    {
        // Если в задании задан csv — берём пересечение с тем, что реально умеет раннер.
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var csv = a.AllowedLanguagesCsv;

        if (!string.IsNullOrWhiteSpace(csv))
        {
            foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var n = NormalizeLang(part);
                if (SupportedImageRunnerLangs.Contains(n)) set.Add(n);
            }
        }

        // дефолт
        if (set.Count == 0)
        {
            set.Add("python");
            set.Add("pascal");
        }

        return set;
    }


    private static bool LooksLikePascal(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var s = code.TrimStart();
        // Very cheap heuristics; we only use them as a safety net.
        if (s.StartsWith("uses ", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("begin", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Contains("end.", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Contains("GraphABC", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Contains("Drawman", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Contains("DrawMan", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool LooksLikePython(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var s = code.TrimStart();
        if (s.StartsWith("import ", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("from ", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Contains("def ", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Contains("print(", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Псевдоним для фронта: финальная отправка решения (рендер + сравнение).
    /// По сути это то же самое, что compare-code.
    /// </summary>
    [HttpPost("submit-code")]
    [RequireQuota(QuotaBuckets.Tasks)]
    public Task<ActionResult<ImageTestCompareResponse>> SubmitCode([FromRoute] Guid assignmentId, [FromBody] CompareCodeRequest req, CancellationToken ct)
        => CompareCode(assignmentId, req, ct);
    private async Task<TaskAssignment?> GetViewableImageAssignmentAsync(Guid assignmentId, CancellationToken ct)
    {
        var a = await _db.TaskAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (a is null) return null;

        var uid = _currentUser.GetUserId();
        var role = _currentUser.GetRole();
        if (!await _access.CanViewCourseAsync(uid, role, a.CourseId))
            throw new UnauthorizedAccessException("Assignment is not доступен пользователю");

        return a;
    }

    private async Task<bool> CanEditAssignmentAsync(TaskAssignment a, CancellationToken ct)
    {
        var uid = _currentUser.GetUserId();
        var role = _currentUser.GetRole();
        return await _access.CanEditCourseAsync(uid, role, a.CourseId);
    }

}
