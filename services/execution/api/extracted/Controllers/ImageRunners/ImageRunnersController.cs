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
            var r = await _imageRunner.RenderDebugAsync(language, GetPingCode(language), null, ct);
            // Никогда не отдаём stdout/stderr на фронт.
            return Ok(new { ok = r.Ok, language });
        }

        var py = await SafePingAsync("python", ct);
        var pas = await SafePingAsync("pascal", ct);
        var cpp = await SafePingAsync("cpp", ct);
        return Ok(new { python = py, pascal = pas, cpp = cpp });
    }

    [HttpPost("{language}/render")]
    public async Task<IActionResult> Render([FromRoute] string language, [FromBody] ImageRunnerRenderRequest req, CancellationToken ct = default)
    {
        var code = req.Code ?? string.Empty;
        byte[]? png;
        try
        {
            png = await _imageRunner.RenderAsync(language, code, req.Stdin, ct);
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
            var r = await _imageRunner.RenderDebugAsync(language, req.Code ?? string.Empty, req.Stdin, ct);
            // Даже в debug-эндпоинте не отдаём stdout/stderr в API ответ.
            return Ok(new { ok = r.Ok, error = r.Error });
        }
        catch (ImageRunnerHttpException ex)
        {
            return BadRequest(new { ok = false, status = (int)ex.StatusCode, error = "render_failed" });
        }
    }

    private static string GetPingCode(string language)
    {
        var lang = (language ?? string.Empty).Trim().ToLowerInvariant();
        return lang switch
        {
            "pascal" => "uses GraphABC;\nbegin\n  SetWindowSize(120, 120);\n  SetBrushColor(clRed);\n  FillRectangle(20,20,100,100);\nend.",
            "cpp" => @"#include <GL/glut.h>
void display(){ glClearColor(1,1,1,1); glClear(GL_COLOR_BUFFER_BIT); glColor3f(1,0,0); glBegin(GL_QUADS); glVertex2f(-0.5f,-0.5f); glVertex2f(0.5f,-0.5f); glVertex2f(0.5f,0.5f); glVertex2f(-0.5f,0.5f); glEnd(); glFlush(); }
int main(int argc,char** argv){ glutInit(&argc, argv); glutInitDisplayMode(GLUT_SINGLE | GLUT_RGB); glutInitWindowSize(120,120); glutCreateWindow(""TaskForge""); glutDisplayFunc(display); glutMainLoop(); return 0; }",
            _ => "import turtle as t\ns=t.Screen(); s.setup(120,120)\nt.forward(20)\nt.done()",
        };
    }

    private async Task<object> SafePingAsync(string lang, CancellationToken ct)
    {
        try
        {
            var r = await _imageRunner.RenderDebugAsync(lang, GetPingCode(lang), null, ct);
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
    public string? Stdin { get; set; }
}


