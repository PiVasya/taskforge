using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using taskforge.Constants;
using taskforge.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
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

    public sealed record CompareCodeRequest(string Language, string Code, bool Debug = true);

    /// <summary>
    /// "Пробный запуск" рисовалки: просто рендер без сравнения.
    /// По желанию можно включить сравнение с эталоном.
    /// </summary>
    public sealed record RunCodeRequest(string Language, string Code, bool Debug = true, bool CompareWithReference = false);

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
        Guid? solutionId = null;
        DebugConsole.Log("ImageTests", $"Compare(multipart) start trace={trace} assignmentId={assignmentId} fileLength={file?.Length ?? 0}");
        _log.LogInformation("Compare (multipart) start trace={Trace} assignmentId={AssignmentId} fileLength={Len}", trace, assignmentId, file?.Length ?? 0);

        var a = await _db.TaskAssignments.FindAsync(new object?[] { assignmentId }, ct);
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
            // NOTE: avoid names referenceUrl/submittedUrl in this nested scope because
            // they are declared later in the same method block.
            var referenceUrl503 = $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey!)}";
            var submittedUrl503 = $"/api/private-files/{Uri.EscapeDataString(submittedKey)}";

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: Math.Round(thresholdPercent, 1),
                Passed: false,
                ReferenceKey: a.ImageTestReferenceKey!,
                SubmittedKey: submittedKey,
                ReferenceUrl: referenceUrl503,
                SubmittedUrl: submittedUrl503,
                Stdout: string.Empty,
                Stderr: string.Empty,
                RunnerError: "Сервис сравнения изображений временно недоступен. Попробуйте позже.",

                SolutionId: null));
        }

        var passed = similarityPercent >= thresholdPercent;

        var referenceUrl = $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}";
        var submittedUrl = $"/api/private-files/{Uri.EscapeDataString(submittedKey)}";

        solutionId = await SaveImageSolutionAsync(
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
            ReferenceKey: a.ImageTestReferenceKey!,
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
        var a = await _db.TaskAssignments.FindAsync(new object?[] { assignmentId }, ct);
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
            // NOTE: avoid names referenceUrl/submittedUrl in this nested scope because
            // they are declared later in the same method block.
            var referenceUrl503 = $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey!)}";
            var submittedUrl503 = $"/api/private-files/{Uri.EscapeDataString(submittedKey)}";

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: Math.Round(thresholdPercent, 1),
                Passed: false,
                ReferenceKey: a.ImageTestReferenceKey!,
                SubmittedKey: submittedKey,
                ReferenceUrl: referenceUrl503,
                SubmittedUrl: submittedUrl503,
                Stdout: "",
                Stderr: "",
                RunnerError: "Сервис сравнения изображений временно недоступен. Попробуйте позже.",

                SolutionId: null));
        }

        var passed = similarityPercent >= thresholdPercent;

        var referenceUrl = $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}";
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
            ReferenceKey: a.ImageTestReferenceKey,
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
        Guid? solutionId = null;
        _log.LogInformation("RunCode start trace={Trace} assignmentId={AssignmentId} lang={Lang} codeLen={Len} debug={Debug} compare={Compare}",
            trace, assignmentId, req.Language, req.Code?.Length ?? 0, req.Debug, req.CompareWithReference);

        var a = await _db.TaskAssignments.FindAsync(new object?[] { assignmentId }, ct);
        if (a is null) return NotFound();
        if (a.Type != TaskAssignmentTypes.ImageTest) return BadRequest("Assignment is not image-test");
        if (string.IsNullOrWhiteSpace(req.Code)) return BadRequest("Code is empty");

		var lang = (req.Language ?? string.Empty).Trim().ToLowerInvariant();
		_log.LogInformation("RunCode normalized trace={Trace} assignmentId={AssignmentId} lang={Lang}", trace, assignmentId, lang);
        if (lang is not ("python" or "pascal")) return BadRequest("Language must be python or pascal");
        // normalize threshold (support old configs: 0..1 as fraction)
        var thresholdPercent = a.ImageTestSimilarityThreshold ?? 90.0;
        if (thresholdPercent <= 1.0) thresholdPercent *= 100.0;

        var validationErr = ValidateImageTestProgram(lang, req.Code);
        if (validationErr is not null)
        {
			_log.LogWarning("RunCode validation failed trace={Trace} assignmentId={AssignmentId} lang={Lang}. Msg={Msg}",
				trace, assignmentId, lang, validationErr);
            // Quota bucket is already consumed by [RequireQuota], but we can return a clear message
            var referenceUrl = string.IsNullOrWhiteSpace(a.ImageTestReferenceKey)
                ? null
                : $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}";

            return Ok(new ImageTestRunResponse(
                Ok: false,
                RenderedKey: null,
                RenderedUrl: null,
                Stdout: string.Empty,
                Stderr: string.Empty,
                RunnerError: validationErr,
                SimilarityPercent: null,
                ThresholdPercent: Math.Round(thresholdPercent, 1),
                Passed: null,
                ReferenceUrl: referenceUrl,
                SolutionId: null));
        }

        var userId = _currentUser.GetUserId();

        ImageRunnerDebugResult? debug = null;
        byte[]? png;
        string stdout = "";
        string stderr = "";
        string? runnerErr = null;

        if (req.Debug)
        {
            debug = await _runner.RenderDebugAsync(lang, req.Code, ct);
            stdout = debug.Stdout;
            stderr = debug.Stderr;
            runnerErr = debug.Error;
            png = debug.PngBytes;

			_log.LogInformation(
				"RunCode runner result trace={Trace} assignmentId={AssignmentId} lang={Lang} ok={Ok} pngBytes={Bytes} stdoutLen={OutLen} stderrLen={ErrLen} errLen={ErrLen2}",
				trace,
				assignmentId,
				lang,
				debug.Ok,
				png?.Length ?? 0,
				(stdout ?? string.Empty).Length,
				(stderr ?? string.Empty).Length,
				(runnerErr ?? string.Empty).Length);

            if (!debug.Ok)
            {
				_log.LogWarning("RunCode runner failed trace={Trace} assignmentId={AssignmentId} lang={Lang}. Err={Err}",
					trace, assignmentId, lang, runnerErr);
                return Ok(new ImageTestRunResponse(
                    Ok: false,
                    RenderedKey: null,
                    RenderedUrl: null,
                    Stdout: stdout,
                    Stderr: stderr,
                    RunnerError: runnerErr,
                    SimilarityPercent: null,
                    ThresholdPercent: null,
                    Passed: null,
                    ReferenceUrl: null,

                    SolutionId: solutionId));
            }
        }
        else
        {
            png = await _runner.RenderAsync(lang, req.Code, ct);
        }

        if (png is null || png.Length == 0)
        {
			_log.LogWarning("RunCode empty image trace={Trace} assignmentId={AssignmentId} lang={Lang} runnerErr={Err}",
				trace, assignmentId, lang, runnerErr);
            return Ok(new ImageTestRunResponse(
                Ok: false,
                RenderedKey: null,
                RenderedUrl: null,
                Stdout: stdout,
                Stderr: stderr,
                RunnerError: runnerErr ?? "Empty image returned",
                SimilarityPercent: null,
                ThresholdPercent: null,
                Passed: null,
                ReferenceUrl: null,

                SolutionId: solutionId));
        }

        // Upload rendered image
		_log.LogInformation("RunCode uploading image trace={Trace} assignmentId={AssignmentId} lang={Lang} bytes={Bytes}",
			trace, assignmentId, lang, png.Length);
        var renderedKey = await _files.UploadBytesAsync(png, "image/png", $"image-tests/previews/{userId}/{assignmentId}", ".png", ct);
        var renderedUrl = $"/api/private-files/{Uri.EscapeDataString(renderedKey)}";

        // Пробник: только рендер, без сравнения.
        solutionId = await SaveImageSolutionAsync(
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
            stdout: stdout,
            stderr: stderr,
            runnerError: runnerErr,
            ct);

        return Ok(new ImageTestRunResponse(
            Ok: true,
            RenderedKey: renderedKey,
            RenderedUrl: renderedUrl,
            Stdout: stdout,
            Stderr: stderr,
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
        var sw = Stopwatch.StartNew();
        ImageRunnerDebugResult? debug = null;
        string stdout = string.Empty;
        string stderr = string.Empty;
        string? runnerErr = null;
        byte[]? png = null;

        var trace = HttpContext.TraceIdentifier;
        DebugConsole.Log("ImageTests", $"CompareCode start trace={trace} assignmentId={assignmentId} lang={req.Language} codeLen={req.Code?.Length ?? 0} debug={req.Debug}");
        _log.LogInformation("CompareCode start trace={Trace} assignmentId={AssignmentId} lang={Lang} codeLen={Len}", trace, assignmentId, req.Language, req.Code?.Length ?? 0);
        var a = await _db.TaskAssignments.FindAsync(new object?[] { assignmentId }, ct);
        if (a is null) return NotFound();
        if (a.Type != TaskAssignmentTypes.ImageTest) return BadRequest("Assignment is not image-test");
        if (string.IsNullOrWhiteSpace(a.ImageTestReferenceKey)) return BadRequest("Reference image is not configured");
        if (string.IsNullOrWhiteSpace(req.Code)) return BadRequest("Code is empty");

        var lang = (req.Language ?? string.Empty).Trim().ToLowerInvariant();
        if (lang is not ("python" or "pascal")) return BadRequest("Language must be python or pascal");

        var userId = _currentUser.GetUserId();
        var codeLen = Encoding.UTF8.GetByteCount(req.Code);

        _log.LogInformation("[ImageTest] compare-code start: assignment={AssignmentId} user={UserId} lang={Lang} bytes={Bytes}", assignmentId, userId, lang, codeLen);


		// normalize threshold (support old configs: 0..1 as fraction)
		var thresholdPercent = a.ImageTestSimilarityThreshold ?? 90.0;
		if (thresholdPercent <= 1.0) thresholdPercent *= 100.0;

		var validationErr = ValidateImageTestProgram(lang, req.Code);
		if (validationErr is not null)
		{
			_log.LogWarning("CompareCode validation failed trace={Trace} assignmentId={AssignmentId} lang={Lang}. Msg={Msg}",
				trace, assignmentId, lang, validationErr);
			var referenceUrl = string.IsNullOrWhiteSpace(a.ImageTestReferenceKey)
				? string.Empty
				: $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}";

			return Ok(new ImageTestCompareResponse(
				Ok: false,
				SimilarityPercent: 0,
				ThresholdPercent: Math.Round(thresholdPercent, 1),
				Passed: false,
				ReferenceKey: a.ImageTestReferenceKey ?? string.Empty,
				SubmittedKey: null,
				ReferenceUrl: referenceUrl,
				SubmittedUrl: null,
				Stdout: string.Empty,
				Stderr: string.Empty,
				RunnerError: validationErr,
				SolutionId: null
			));
		}

		if (req.Debug)
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
                    ReferenceKey: a.ImageTestReferenceKey,
                    SubmittedKey: null,
                    ReferenceUrl: $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}",
                    SubmittedUrl: null,
                    Stdout: stdout,
                    Stderr: stderr,
                    RunnerError: runnerErr,

                    SolutionId: null));
            }
        }
		else
        {
            png = await _runner.RenderAsync(lang, req.Code, ct);
        }

        if (png is null || png.Length == 0)
        {
            _log.LogWarning("[ImageTest] runner returned empty image: assignment={AssignmentId} user={UserId} lang={Lang}", assignmentId, userId, lang);
            return Ok(new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: Math.Round(thresholdPercent, 1),
                Passed: false,
                ReferenceKey: a.ImageTestReferenceKey,
                SubmittedKey: null,
                ReferenceUrl: $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}",
                SubmittedUrl: null,
                Stdout: stdout,
                Stderr: stderr,
                RunnerError: runnerErr ?? "Empty image returned",

                SolutionId: null));
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
                stdout: stdout,
                stderr: stderr,
                runnerError: "Сервис сравнения изображений временно недоступен. Попробуйте позже.",
                ct);

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: Math.Round(thresholdPercent, 1),
                Passed: false,
                ReferenceKey: a.ImageTestReferenceKey,
                SubmittedKey: submittedKey,
                ReferenceUrl: $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}",
                SubmittedUrl: $"/api/private-files/{Uri.EscapeDataString(submittedKey)}",
                Stdout: stdout,
                Stderr: stderr,
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
                stdout: stdout,
                stderr: stderr,
                runnerError: runnerErr ?? $"Image similarity failed: {ex.GetType().Name}: {ex.Message}",
                ct);

            return Ok(new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: Math.Round(thresholdPercent, 1),
                Passed: false,
                ReferenceKey: a.ImageTestReferenceKey,
                SubmittedKey: submittedKey,
                ReferenceUrl: $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}",
                SubmittedUrl: $"/api/private-files/{Uri.EscapeDataString(submittedKey)}",
                Stdout: stdout,
                Stderr: stderr,
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
            stdout: stdout,
            stderr: stderr,
            runnerError: runnerErr,
            ct);

        var response = new ImageTestCompareResponse(
            Ok: true,
            SimilarityPercent: Math.Round(similarityPercent, 1),
            ThresholdPercent: Math.Round(thresholdPercent, 1),
            Passed: passed,
            ReferenceKey: a.ImageTestReferenceKey,
            SubmittedKey: submittedKey,
            ReferenceUrl: $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}",
            SubmittedUrl: $"/api/private-files/{Uri.EscapeDataString(submittedKey)}",
            Stdout: stdout,
            Stderr: stderr,
            RunnerError: runnerErr,
            SolutionId: solutionId);

        return Ok(response);
    }

    /// <summary>
    /// Псевдоним для фронта: финальная отправка решения (рендер + сравнение).
    /// По сути это то же самое, что compare-code.
    /// </summary>
    [HttpPost("submit-code")]
    [RequireQuota(QuotaBuckets.Tasks)]
    public Task<ActionResult<ImageTestCompareResponse>> SubmitCode([FromRoute] Guid assignmentId, [FromBody] CompareCodeRequest req, CancellationToken ct)
        => CompareCode(assignmentId, req, ct);

	private static string? ValidateImageTestProgram(string language, string? code)
	{
		var s = code ?? string.Empty;
		if (string.IsNullOrWhiteSpace(s)) return "Код пустой";

		if (string.Equals(language, "python", StringComparison.OrdinalIgnoreCase))
		{
			// Accept both `import turtle` and `from turtle import *`
			if (!Regex.IsMatch(s, @"\b(import\s+turtle|from\s+turtle\s+import)\b", RegexOptions.IgnoreCase))
				return "Для проверки картинок в Python нужно рисовать через turtle. Добавь `import turtle` (или `from turtle import ...`) и используй turtle-графику.";
			return null;
		}

		if (string.Equals(language, "pascal", StringComparison.OrdinalIgnoreCase))
		{
			// Supported units for drawing in PascalABC.NET
			var hasGraph = s.IndexOf("GraphABC", StringComparison.OrdinalIgnoreCase) >= 0
			           || s.IndexOf("GraphWPF", StringComparison.OrdinalIgnoreCase) >= 0
			           || s.IndexOf("ABCObjects", StringComparison.OrdinalIgnoreCase) >= 0;
			var hasDrawMan = s.IndexOf("DrawMan", StringComparison.OrdinalIgnoreCase) >= 0
			           || s.IndexOf("Drawman", StringComparison.OrdinalIgnoreCase) >= 0;
			var hasTurtle = Regex.IsMatch(s, @"\bTurtle\b", RegexOptions.IgnoreCase);

			if (!hasGraph && !hasDrawMan && !hasTurtle)
				return "Для проверки картинок в Pascal нужно рисовать через один из модулей: GraphABC / GraphWPF / ABCObjects / Turtle / DrawMan. Сейчас в коде их не найдено.";
			return null;
		}

		return null;
	}
}
