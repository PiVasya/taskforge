using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Constants;
using taskforge.Data;
using taskforge.Services.Files;
using taskforge.Services.ImageRunners;
using taskforge.Services.ImageTests;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Assignments;

[ApiController]
[Authorize]
[Route("api/assignments/{assignmentId:guid}/image-tests")]
public sealed class ImageTestsController : ControllerBase
{
    private readonly ILogger<ImageTestsController> _log;
    private readonly AppDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly ICurrentUserService _currentUser;
    private readonly IImageRunnerClient _runner;
    private readonly IImageSimilarityService _similarity;

    public ImageTestsController(
        ILogger<ImageTestsController> log,
        AppDbContext db,
        IFileStorageService storage,
        ICurrentUserService currentUser,
        IImageRunnerClient runner,
        IImageSimilarityService similarity)
    {
        _log = log;
        _db = db;
        _storage = storage;
        _currentUser = currentUser;
        _runner = runner;
        _similarity = similarity;
    }

    public sealed class CompareUploadedImageRequest
    {
        // фронт может прислать разные имена поля — принимаем оба
        [JsonPropertyName("submittedImageBase64")]
        public string? SubmittedImageBase64 { get; init; }

        [JsonPropertyName("base64Png")]
        public string? Base64Png { get; init; }

        public string GetBase64() => (SubmittedImageBase64 ?? Base64Png ?? string.Empty).Trim();
    }

    public sealed class CompareCodeRequest
    {
        [JsonPropertyName("lang")]
        public string? Lang { get; init; }

        [JsonPropertyName("language")]
        public string? Language { get; init; }

        [JsonPropertyName("code")]
        public string? Code { get; init; }

        [JsonPropertyName("debug")]
        public bool? Debug { get; init; }

        public string GetLang() => (Lang ?? Language ?? string.Empty).Trim();
    }

    public sealed record ImageTestCompareResponse(
        bool Ok,
        double SimilarityPercent,
        double ThresholdPercent,
        bool Passed,
        string Message,
        string? SubmittedKey,
        string ReferenceKey,
        string? RunnerError,
        string? Stdout,
        string? Stderr,
        string? TraceId);

    [HttpPost("compare-upload")]
    public async Task<ActionResult<ImageTestCompareResponse>> CompareUpload(
        [FromRoute] Guid assignmentId,
        [FromBody] CompareUploadedImageRequest req,
        CancellationToken ct)
    {
        var traceId = HttpContext.TraceIdentifier;
        var userId = _currentUser.GetUserId();
        using var _ = _log.BeginScope(new Dictionary<string, object?>
        {
            ["TraceId"] = traceId,
            ["Endpoint"] = "image-tests/compare-upload",
            ["AssignmentId"] = assignmentId,
            ["UserId"] = userId
        });

        var totalSw = Stopwatch.StartNew();
        _log.LogInformation("[ImageTest] compare-upload start");

        try
        {
            var aSw = Stopwatch.StartNew();
            var a = await _db.TaskAssignments.FindAsync(new object?[] { assignmentId }, ct);
            aSw.Stop();
            _log.LogInformation("[ImageTest] assignment load done in {Ms}ms (found={Found})", aSw.ElapsedMilliseconds, a is not null);

            if (a is null)
            {
                return NotFound();
            }

            if (a.AssignmentType != TaskAssignmentType.ImageTest)
            {
                _log.LogWarning("[ImageTest] invalid assignment type: {Type}", a.AssignmentType);
                return BadRequest("Assignment is not image-test");
            }

            if (string.IsNullOrWhiteSpace(a.ReferenceImageKey))
            {
                _log.LogWarning("[ImageTest] assignment reference key is empty");
                return BadRequest("Reference image is not configured");
            }

            var threshold = a.ImageTestThresholdPercent ?? 90.0;
            _log.LogInformation("[ImageTest] threshold={ThresholdPercent}% referenceKey={ReferenceKey}", threshold, a.ReferenceImageKey);

            var base64 = req.GetBase64();
            _log.LogInformation("[ImageTest] submitted base64 len={Len}", base64.Length);
            if (string.IsNullOrWhiteSpace(base64))
            {
                return BadRequest("SubmittedImageBase64 is required");
            }

            byte[] submittedBytes;
            var decodeSw = Stopwatch.StartNew();
            try
            {
                submittedBytes = Convert.FromBase64String(base64);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[ImageTest] base64 decode failed");
                return BadRequest("Invalid base64 payload");
            }
            finally
            {
                decodeSw.Stop();
                _log.LogInformation("[ImageTest] base64 decode done in {Ms}ms", decodeSw.ElapsedMilliseconds);
            }

            _log.LogInformation("[ImageTest] submitted png bytes={Bytes}", submittedBytes.Length);

            var refSw = Stopwatch.StartNew();
            var (refStream, refCt) = await _storage.GetFileAsync(a.ReferenceImageKey!, ct);
            refSw.Stop();
            _log.LogInformation("[ImageTest] reference load done in {Ms}ms (contentType={ContentType})", refSw.ElapsedMilliseconds, refCt);

            var simSw = Stopwatch.StartNew();
            var similarity = await _similarity.ComputeSimilarityPercentAsync(submittedBytes, refStream, ct);
            simSw.Stop();
            _log.LogInformation("[ImageTest] similarity computed in {Ms}ms => {Similarity:F2}%", simSw.ElapsedMilliseconds, similarity);

            var passed = similarity >= threshold;
            var message = passed ? "Image test passed" : "Image test failed";

            totalSw.Stop();
            _log.LogInformation("[ImageTest] compare-upload end in {Ms}ms (passed={Passed})", totalSw.ElapsedMilliseconds, passed);

            return Ok(new ImageTestCompareResponse(
                Ok: true,
                SimilarityPercent: similarity,
                ThresholdPercent: threshold,
                Passed: passed,
                Message: message,
                SubmittedKey: null,
                ReferenceKey: a.ReferenceImageKey!,
                RunnerError: null,
                Stdout: null,
                Stderr: null,
                TraceId: traceId));
        }
        catch (Exception ex)
        {
            totalSw.Stop();
            _log.LogError(ex, "[ImageTest] compare-upload failed in {Ms}ms", totalSw.ElapsedMilliseconds);

            return Ok(new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: 0,
                Passed: false,
                Message: "Internal error",
                SubmittedKey: null,
                ReferenceKey: string.Empty,
                RunnerError: ex.Message,
                Stdout: null,
                Stderr: null,
                TraceId: traceId));
        }
    }

    [HttpPost("compare-code")]
    public async Task<ActionResult<ImageTestCompareResponse>> CompareCode(
        [FromRoute] Guid assignmentId,
        [FromBody] CompareCodeRequest req,
        CancellationToken ct)
    {
        var traceId = HttpContext.TraceIdentifier;
        var userId = _currentUser.GetUserId();
        using var _ = _log.BeginScope(new Dictionary<string, object?>
        {
            ["TraceId"] = traceId,
            ["Endpoint"] = "image-tests/compare-code",
            ["AssignmentId"] = assignmentId,
            ["UserId"] = userId
        });

        var totalSw = Stopwatch.StartNew();
        _log.LogInformation("[ImageTest] compare-code start");

        try
        {
            var aSw = Stopwatch.StartNew();
            var a = await _db.TaskAssignments.FindAsync(new object?[] { assignmentId }, ct);
            aSw.Stop();
            _log.LogInformation("[ImageTest] assignment load done in {Ms}ms (found={Found})", aSw.ElapsedMilliseconds, a is not null);

            if (a is null)
            {
                return NotFound();
            }

            if (a.AssignmentType != TaskAssignmentType.ImageTest)
            {
                _log.LogWarning("[ImageTest] invalid assignment type: {Type}", a.AssignmentType);
                return BadRequest("Assignment is not image-test");
            }

            if (string.IsNullOrWhiteSpace(a.ReferenceImageKey))
            {
                _log.LogWarning("[ImageTest] assignment reference key is empty");
                return BadRequest("Reference image is not configured");
            }

            var threshold = a.ImageTestThresholdPercent ?? 90.0;
            var lang = req.GetLang();
            var debug = req.Debug == true;
            var code = req.Code ?? string.Empty;

            _log.LogInformation(
                "[ImageTest] request: lang='{Lang}' debug={Debug} threshold={Threshold}% codeLen={CodeLen}",
                lang,
                debug,
                threshold,
                code.Length);

            if (string.IsNullOrWhiteSpace(lang))
            {
                return BadRequest("Language is required");
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return BadRequest("Code is required");
            }

            byte[] renderedBytes;
            string? stdout = null;
            string? stderr = null;
            string? runnerError = null;

            var renderSw = Stopwatch.StartNew();
            try
            {
                if (debug && lang.Equals("python", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("[ImageTest] calling runner RenderDebugAsync");
                    var dbg = await _runner.RenderDebugAsync(lang, code, traceId, ct);
                    renderedBytes = dbg.PngBytes;
                    stdout = dbg.Stdout;
                    stderr = dbg.Stderr;
                }
                else
                {
                    _log.LogInformation("[ImageTest] calling runner RenderAsync");
                    renderedBytes = await _runner.RenderAsync(lang, code, traceId, ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[ImageTest] runner failed");
                runnerError = ex.Message;
                return Ok(new ImageTestCompareResponse(
                    Ok: false,
                    SimilarityPercent: 0,
                    ThresholdPercent: threshold,
                    Passed: false,
                    Message: "Runner error",
                    SubmittedKey: null,
                    ReferenceKey: a.ReferenceImageKey!,
                    RunnerError: runnerError,
                    Stdout: stdout,
                    Stderr: stderr,
                    TraceId: traceId));
            }
            finally
            {
                renderSw.Stop();
                _log.LogInformation("[ImageTest] runner call done in {Ms}ms", renderSw.ElapsedMilliseconds);
            }

            _log.LogInformation("[ImageTest] rendered bytes={Bytes}", renderedBytes.Length);
            if (!string.IsNullOrWhiteSpace(stdout))
                _log.LogInformation("[ImageTest] runner stdout len={Len}", stdout.Length);
            if (!string.IsNullOrWhiteSpace(stderr))
                _log.LogWarning("[ImageTest] runner stderr len={Len}", stderr.Length);

            var submittedKey = $"image-tests/{assignmentId}/{userId}/{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.png";
            var uploadSw = Stopwatch.StartNew();
            await _storage.SaveFileAsync(submittedKey, renderedBytes, "image/png", ct);
            uploadSw.Stop();
            _log.LogInformation("[ImageTest] uploaded rendered image: key={Key} bytes={Bytes} in {Ms}ms", submittedKey, renderedBytes.Length, uploadSw.ElapsedMilliseconds);

            var refSw = Stopwatch.StartNew();
            var (refStream, refCt) = await _storage.GetFileAsync(a.ReferenceImageKey!, ct);
            refSw.Stop();
            _log.LogInformation("[ImageTest] reference load done in {Ms}ms (contentType={ContentType})", refSw.ElapsedMilliseconds, refCt);

            var simSw = Stopwatch.StartNew();
            var similarity = await _similarity.ComputeSimilarityPercentAsync(renderedBytes, refStream, ct);
            simSw.Stop();
            _log.LogInformation("[ImageTest] similarity computed in {Ms}ms => {Similarity:F2}%", simSw.ElapsedMilliseconds, similarity);

            var passed = similarity >= threshold;
            var message = passed ? "Image test passed" : "Image test failed";

            totalSw.Stop();
            _log.LogInformation("[ImageTest] compare-code end in {Ms}ms: passed={Passed}", totalSw.ElapsedMilliseconds, passed);

            return Ok(new ImageTestCompareResponse(
                Ok: true,
                SimilarityPercent: similarity,
                ThresholdPercent: threshold,
                Passed: passed,
                Message: message,
                SubmittedKey: submittedKey,
                ReferenceKey: a.ReferenceImageKey!,
                RunnerError: runnerError,
                Stdout: stdout,
                Stderr: stderr,
                TraceId: traceId));
        }
        catch (Exception ex)
        {
            totalSw.Stop();
            _log.LogError(ex, "[ImageTest] compare-code failed in {Ms}ms", totalSw.ElapsedMilliseconds);

            return Ok(new ImageTestCompareResponse(
                Ok: false,
                SimilarityPercent: 0,
                ThresholdPercent: 0,
                Passed: false,
                Message: "Internal error",
                SubmittedKey: null,
                ReferenceKey: string.Empty,
                RunnerError: ex.Message,
                Stdout: null,
                Stderr: null,
                TraceId: traceId));
        }
    }
}
