using Microsoft.AspNetCore.Mvc;
using taskforge.Services.ImageRunners;

namespace taskforge.Controllers.ImageRunners;

[ApiController]
[Route("api/image-runners")]
public sealed class ImageRunnersController : ControllerBase
{
    private readonly IImageRunnerClient _imageRunner;
    private readonly ILogger<ImageRunnersController> _log;

    public ImageRunnersController(IImageRunnerClient imageRunner, ILogger<ImageRunnersController> log)
    {
        _imageRunner = imageRunner;
        _log = log;
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health([FromQuery] string? language = null, CancellationToken ct = default)
    {
        var traceId = HttpContext.TraceIdentifier;
        _log.LogInformation("[ImageRunner {TraceId}] Health check: language={Language}", traceId, language);
        // Простая проверка живости: дергаем /render/debug на пустом коде? Не надо.
        // Раннер сам имеет /health, но тут мы просто проверяем доступность базового URL.
        // Чтобы не плодить новый клиент, используем RenderDebugAsync с коротким кодом.

        if (!string.IsNullOrWhiteSpace(language))
        {
            // Минимальная программа, которая создаёт пустой кадр через turtle.
            var pingCode = "import turtle as t\ns=t.Screen(); s.setup(10,10)\nt.done()";
            var r = await _imageRunner.RenderDebugAsync(language, pingCode, traceId, ct);
            return Ok(new { ok = r.Ok, language, stdout = r.Stdout, stderr = r.Stderr, error = r.Error });
        }

        var py = await SafePingAsync("python", ct);
        var pas = await SafePingAsync("pascal", ct);
        return Ok(new { python = py, pascal = pas });
    }

    [HttpPost("{language}/render")]
    public async Task<IActionResult> Render([FromRoute] string language, [FromBody] ImageRunnerRenderRequest req, CancellationToken ct = default)
    {
        var traceId = HttpContext.TraceIdentifier;
        var codeLen = req.Code?.Length ?? 0;
        _log.LogInformation("[ImageRunner {TraceId}] Render: language={Language}, codeLen={CodeLen}", traceId, language, codeLen);

        var png = await _imageRunner.RenderAsync(language, req.Code ?? string.Empty, traceId, ct);
        if (png is null)
        {
            _log.LogWarning("[ImageRunner {TraceId}] Render returned null (runner error)", traceId);
            return StatusCode(502, new { error = "Runner returned null" });
        }

        return File(png, "image/png");
    }

    [HttpPost("{language}/render/debug")]
    public async Task<IActionResult> RenderDebug([FromRoute] string language, [FromBody] ImageRunnerRenderRequest req, CancellationToken ct = default)
    {
        var traceId = HttpContext.TraceIdentifier;
        var codeLen = req.Code?.Length ?? 0;
        _log.LogInformation("[ImageRunner {TraceId}] RenderDebug: language={Language}, codeLen={CodeLen}", traceId, language, codeLen);
        var r = await _imageRunner.RenderDebugAsync(language, req.Code ?? string.Empty, traceId, ct);
        return Ok(r);
    }

    private async Task<object> SafePingAsync(string lang, CancellationToken ct)
    {
        var traceId = HttpContext.TraceIdentifier;
        try
        {
            var pingCode = "import turtle as t\ns=t.Screen(); s.setup(10,10)\nt.done()";
            var r = await _imageRunner.RenderDebugAsync(lang, pingCode, traceId, ct);
            return new { ok = r.Ok, stdout = r.Stdout, stderr = r.Stderr, error = r.Error };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }
}

public sealed class ImageRunnerRenderRequest
{
    public string? Code { get; set; }
}
