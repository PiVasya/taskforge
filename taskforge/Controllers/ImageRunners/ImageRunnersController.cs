using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Services.ImageRunners;

namespace taskforge.Controllers.ImageRunners;

[Authorize]
[ApiController]
[Route("api/image-runners")]
public sealed class ImageRunnersController : ControllerBase
{
    private readonly IImageRunnerClient _imageRunner;

    public ImageRunnersController(IImageRunnerClient imageRunner)
    {
        _imageRunner = imageRunner;
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health([FromQuery] string? language = null, CancellationToken ct = default)
    {
        // Простая проверка живости: дергаем /render/debug на пустом коде? Не надо.
        // Раннер сам имеет /health, но тут мы просто проверяем доступность базового URL.
        // Чтобы не плодить новый клиент, используем RenderDebugAsync с коротким кодом.

        if (!string.IsNullOrWhiteSpace(language))
        {
            // Минимальная программа, которая создаёт пустой кадр через turtle.
            var pingCode = "import turtle as t\ns=t.Screen(); s.setup(10,10)\nt.done()";
            var r = await _imageRunner.RenderDebugAsync(language, pingCode, ct);
            // Никогда не отдаём stdout/stderr на фронт.
            return Ok(new { ok = r.Ok, language });
        }

        var py = await SafePingAsync("python", ct);
        var pas = await SafePingAsync("pascal", ct);
        return Ok(new { python = py, pascal = pas });
    }

    [HttpPost("{language}/render")]
    public async Task<IActionResult> Render([FromRoute] string language, [FromBody] ImageRunnerRenderRequest req, CancellationToken ct = default)
    {
        var code = req.Code ?? string.Empty;
        byte[]? png;
        try
        {
            png = await _imageRunner.RenderAsync(language, code, ct);
        }
        catch (ImageRunnerHttpException ex)
        {
            // Никогда не отдаём тело ответа раннера (там могут быть debug-логи/stdout/stderr).
            return BadRequest(new { ok = false, status = (int)ex.StatusCode, error = "render_failed" });
        }

        if (png is { Length: > 0 })
            return File(png, "image/png");

        // Если раннер вернул пусто/NULL — тоже возвращаем без логов.
        return BadRequest(new { ok = false, error = "empty_image" });
    }

    [HttpPost("{language}/render/debug")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RenderDebug([FromRoute] string language, [FromBody] ImageRunnerRenderRequest req, CancellationToken ct = default)
    {
        try
        {
            var r = await _imageRunner.RenderDebugAsync(language, req.Code ?? string.Empty, ct);
            // Даже в debug-эндпоинте не отдаём stdout/stderr в API ответ.
            return Ok(new { ok = r.Ok, error = r.Error });
        }
        catch (ImageRunnerHttpException ex)
        {
            return BadRequest(new { ok = false, status = (int)ex.StatusCode, error = "render_failed" });
        }
    }

    private async Task<object> SafePingAsync(string lang, CancellationToken ct)
    {
        try
        {
            var pingCode = "import turtle as t\ns=t.Screen(); s.setup(10,10)\nt.done()";
            var r = await _imageRunner.RenderDebugAsync(lang, pingCode, ct);
            return new { ok = r.Ok };
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
