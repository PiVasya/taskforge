using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using taskforge.Helpers;

namespace taskforge.Services.ImageRunners;

public sealed class HttpImageRunnerClient : IImageRunnerClient
{
    private readonly HttpClient _http;
    private readonly ImageRunnersOptions _opt;
    private readonly ILogger<HttpImageRunnerClient> _log;

    public HttpImageRunnerClient(HttpClient http, IOptions<ImageRunnersOptions> opt, ILogger<HttpImageRunnerClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<byte[]?> RenderAsync(string language, string sourceCode, CancellationToken ct = default)
    {
        var baseUrl = GetBaseUrl(language);
        var url = new Uri(new Uri(baseUrl), "/render");

        DebugConsole.Log("ImageRunner", $"RenderAsync start lang={language} baseUrl={baseUrl} codeLen={sourceCode?.Length ?? 0}");

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(BuildBody(language, sourceCode))
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            var err = await SafeReadBodyAsync(resp, ct);
            DebugConsole.Log("ImageRunner", $"RenderAsync fail lang={language} status={(int)resp.StatusCode} ms={sw.ElapsedMilliseconds} body={Truncate(err)}");
            _log.LogWarning("[ImageRunner] Render failed {Lang} {Status} in {Ms}ms. Body: {Body}",
                language, (int)resp.StatusCode, sw.ElapsedMilliseconds, Truncate(err));
            return null;
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        DebugConsole.Log("ImageRunner", $"RenderAsync ok lang={language} bytes={bytes.Length} ms={sw.ElapsedMilliseconds}");
        _log.LogInformation("[ImageRunner] Render ok {Lang} bytes={Bytes} in {Ms}ms", language, bytes.Length, sw.ElapsedMilliseconds);
        return bytes;
    }

    public async Task<ImageRunnerDebugResult> RenderDebugAsync(string language, string sourceCode, CancellationToken ct = default)
    {
        // Not all runners implement /render/debug.
        // We support it for Python and Pascal (both return stdout/stderr + base64 image).
        var supportsDebug = string.Equals(language, "python", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(language, "pascal", StringComparison.OrdinalIgnoreCase);

        if (!supportsDebug)
        {
            var png = await RenderAsync(language, sourceCode, ct);
            return new ImageRunnerDebugResult
            {
                Ok = png != null,
                Error = png == null ? "Render failed" : null,
                PngBytes = png
            };
        }

        var baseUrl = GetBaseUrl(language);
        var url = new Uri(new Uri(baseUrl), "/render/debug");

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(BuildBody(language, sourceCode))
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        sw.Stop();

        if (resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            var ok = JsonSerializer.Deserialize<RenderDebugResponse>(json, JsonOpts);

            var pngBytes = ok?.PngBase64 != null ? Convert.FromBase64String(ok.PngBase64) : null;

            _log.LogInformation("[ImageRunner] RenderDebug ok {Lang} bytes={Bytes} in {Ms}ms", language, pngBytes?.Length ?? 0, sw.ElapsedMilliseconds);

            return new ImageRunnerDebugResult
            {
                Ok = true,
                Stdout = ok?.Stdout ?? string.Empty,
                Stderr = ok?.Stderr ?? string.Empty,
                PngBytes = pngBytes
            };
        }

        // Error payload is usually {"detail": {"message":..., "stdout":..., "stderr":...}}
        var body = await SafeReadBodyAsync(resp, ct);
        var (msg, stdout, stderr) = TryParseDetail(body);

        _log.LogWarning("[ImageRunner] RenderDebug failed {Lang} {Status} in {Ms}ms. Msg: {Msg}",
            language, (int)resp.StatusCode, sw.ElapsedMilliseconds, Truncate(msg ?? body));

        return new ImageRunnerDebugResult
        {
            Ok = false,
            Error = msg ?? body,
            Stdout = stdout ?? string.Empty,
            Stderr = stderr ?? string.Empty,
            PngBytes = null
        };
    }

    private string GetBaseUrl(string language)
    {
        language = language?.Trim().ToLowerInvariant() ?? "";
        return language switch
        {
            "python" => _opt.PythonUrl,
            "pascal" => _opt.PascalUrl,
            _ => throw new ArgumentException($"Unsupported image-runner language: {language}")
        };
    }

    private static object BuildBody(string language, string sourceCode)
    {
        language = language?.Trim().ToLowerInvariant() ?? "";
        return language switch
        {
            "python" => new { code = sourceCode },
            "pascal" => new { source = sourceCode },
            _ => new { code = sourceCode }
        };
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    private static string Truncate(string? s, int max = 500)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max] + "...";
    }

    private static (string? msg, string? stdout, string? stderr) TryParseDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("detail", out var detail)) return (null, null, null);

            string? msg = detail.TryGetProperty("message", out var m) ? m.GetString() : null;
            string? stdout = detail.TryGetProperty("stdout", out var o) ? o.GetString() : null;
            string? stderr = detail.TryGetProperty("stderr", out var e) ? e.GetString() : null;
            return (msg, stdout, stderr);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private sealed class RenderDebugResponse
    {
        public string? PngBase64 { get; set; }
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
