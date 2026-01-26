using System.Text;
using Microsoft.AspNetCore.Authorization;
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
        string? RunnerError);

    /// <summary>
    /// Compare a user-uploaded image (multipart/form-data) against the reference image.
    /// This endpoint accepts an uploaded file and returns similarity information.
    /// It matches the old front-end call /image-test/compare.
    /// </summary>
    [HttpPost("compare")]
    public async Task<ActionResult<ImageTestCompareResponse>> Compare(
        [FromRoute] Guid assignmentId,
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        var trace = HttpContext.TraceIdentifier;
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

        // Determine mime type; fallback to file.ContentType or default to image/png
        var mime = !string.IsNullOrWhiteSpace(file.ContentType) ? file.ContentType : "image/png";

        // Upload submitted image
        var submittedKey = await _files.UploadBytesAsync(submittedBytes, mime, $"image-tests/submissions/{userId}/{assignmentId}", ".png", ct);

        // Compare with reference
        var (refStreamRaw, _) = await _files.GetAsync(a.ImageTestReferenceKey, ct);
        await using var refStream = refStreamRaw;
        await using var subStream = new MemoryStream(submittedBytes);

        var similarityPercent = await _similarity.GetSimilarityPercentAsync(refStream, subStream, ct);

        // Threshold can be stored either as 0..1 or 0..100 (legacy). Normalize to percent.
        var thresholdPercent = a.ImageTestSimilarityThreshold ?? 70.0;
        if (thresholdPercent <= 1.0) thresholdPercent *= 100.0;

        var passed = similarityPercent >= thresholdPercent;

        var referenceUrl = $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}";
        var submittedUrl = $"/api/private-files/{Uri.EscapeDataString(submittedKey)}";

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
            RunnerError: null));
    }

    [HttpPost("compare-upload")]
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

        var similarityPercent = await _similarity.GetSimilarityPercentAsync(refStream, subStream, ct);

        // Threshold can be stored either as 0..1 or 0..100 (legacy). Normalize to percent.
        var thresholdPercent = a.ImageTestSimilarityThreshold ?? 70.0;
        if (thresholdPercent <= 1.0) thresholdPercent *= 100.0;

        var passed = similarityPercent >= thresholdPercent;

        var referenceUrl = $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey)}";
        var submittedUrl = $"/api/private-files/{Uri.EscapeDataString(submittedKey)}";

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
            RunnerError: null));
    }

    /// <summary>
    /// Главная штука: пользователь присылает код (Python/Pascal),
    /// backend рендерит картинку внутри контейнера и сравнивает с эталоном.
    /// Пользователь не загружает изображения вручную.
    /// </summary>
    [HttpPost("compare-code")]
    public async Task<ActionResult<ImageTestCompareResponse>> CompareCode([FromRoute] Guid assignmentId, [FromBody] CompareCodeRequest req, CancellationToken ct)
    {
        var trace = HttpContext.TraceIdentifier;
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

        var sw = System.Diagnostics.Stopwatch.StartNew();

        ImageRunnerDebugResult? debug = null;
        byte[]? png;
        string stdout = "";
        string stderr = "";
        string? runnerErr = null;

        // normalize threshold (support old configs: 0..1 as fraction)
        var thresholdPercent = a.ImageTestSimilarityThreshold ?? 90.0;
        if (thresholdPercent <= 1.0) thresholdPercent *= 100.0;

        if (req.Debug && lang == "python")
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
                    RunnerError: runnerErr));
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
                RunnerError: runnerErr ?? "Empty image returned"));
        }

        // Upload submitted image
        var submittedKey = await _files.UploadBytesAsync(png, "image/png", $"image-tests/submissions/{userId}/{assignmentId}", ".png", ct);

        // Compare with reference
        var (refStream, _) = await _files.GetAsync(a.ImageTestReferenceKey, ct);
        await using var refS = refStream;
        await using var subStream = new MemoryStream(png);

        var similarityPercent = await _similarity.GetSimilarityPercentAsync(refS, subStream, ct);
        var passed = similarityPercent >= thresholdPercent;

        sw.Stop();
        _log.LogInformation("[ImageTest] compare-code done: assignment={AssignmentId} user={UserId} lang={Lang} similarity={Similarity:0.000} passed={Passed} ms={Ms}",
            assignmentId, userId, lang, similarityPercent, passed, sw.ElapsedMilliseconds);

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
            RunnerError: runnerErr);

        return Ok(response);
    }
}
