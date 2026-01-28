using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenCvSharp;
using taskforge.Services.ImageTests;

namespace taskforge.Controllers.Diagnostics;

[ApiController]
[Route("api/diagnostics/image-test")]
[Authorize]
public sealed class ImageTestDiagnosticsController : ControllerBase
{
    public sealed record ImageTestDiagResponse(
        bool Ok,
        string Dotnet,
        string OS,
        string ProcessArch,
        string? OpenCvVersion,
        string? OpenCvBuildInfoHead,
        string? Error,
        string? ExceptionType,
        string? Stack);

    /// <summary>
    /// Быстрая проверка, что OpenCvSharp и нативная библиотека реально загружаются в контейнере.
    /// Полезно, когда "сборка проходит", но на рантайме падает DllNotFoundException.
    /// </summary>
    [HttpGet]
    public ActionResult<ImageTestDiagResponse> Get()
    {
        var trace = HttpContext.TraceIdentifier;
        using var tscope = ImageTestTraceContext.Begin($"{trace}-diag");

        try
        {
            ImageTestTraceContext.ConsoleLog(true, "Diagnostics: starting OpenCV self-check");

            // 1) Минимальная операция (создать матрицу, нарисовать линию) — заставляет подгрузиться нативному коду.
            using var m = new Mat(new Size(32, 32), MatType.CV_8UC3, Scalar.All(0));
            Cv2.Line(m, new Point(0, 0), new Point(31, 31), new Scalar(255, 255, 255), 1);
            var sum = Cv2.Sum(m);

            // 2) Версии/информация о сборке OpenCV.
            string? ver = null;
            string? head = null;
            try
            {
                // GetVersionString есть не во всех версиях OpenCvSharp, поэтому защищаем try/catch.
                ver = Cv2.GetVersionString();
            }
            catch
            {
                // ignore
            }

            try
            {
                var bi = Cv2.GetBuildInformation();
                if (!string.IsNullOrWhiteSpace(bi))
                    head = bi.Length <= 500 ? bi : bi[..500] + "...";
            }
            catch
            {
                // ignore
            }

            ImageTestTraceContext.ConsoleLog(true, $"Diagnostics: OpenCV ok. Sum={sum.Val0:0} Ver={(ver ?? "(unknown)")}");

            return Ok(new ImageTestDiagResponse(
                Ok: true,
                Dotnet: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OS: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ProcessArch: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                OpenCvVersion: ver,
                OpenCvBuildInfoHead: head,
                Error: null,
                ExceptionType: null,
                Stack: null));
        }
        catch (Exception ex)
        {
            ImageTestTraceContext.ConsoleLog(true, $"Diagnostics: FAILED: {ex.GetType().Name}: {ex.Message}");
            return Ok(new ImageTestDiagResponse(
                Ok: false,
                Dotnet: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OS: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ProcessArch: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                OpenCvVersion: null,
                OpenCvBuildInfoHead: null,
                Error: ex.Message,
                ExceptionType: ex.GetType().FullName,
                Stack: ex.StackTrace));
        }
    }
}
