using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace taskforge.Services.ImageTests;

public sealed class HttpImageAnalyzerClient : IImageAnalyzerClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpImageAnalyzerClient> _logger;
    private readonly ImageAnalyzerOptions _opt;

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HttpImageAnalyzerClient(
        HttpClient http,
        ILogger<HttpImageAnalyzerClient> logger,
        IOptions<ImageAnalyzerOptions> opt)
    {
        _http = http;
        _logger = logger;
        _opt = opt.Value;
    }

    public async Task<ImageAnalyzerCompareResult?> CompareAsync(
        byte[] expected,
        byte[] actual,
        CancellationToken ct = default)
    {
        if (!_opt.Enabled)
            return null;

        if (string.IsNullOrWhiteSpace(_opt.Url) || _http.BaseAddress is null)
            return null;

        using var form = new MultipartFormDataContent();

        var exp = new ByteArrayContent(expected);
        exp.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        form.Add(exp, "expected", "expected.png");

        var act = new ByteArrayContent(actual);
        act.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        form.Add(act, "actual", "actual.png");

        // threshold влияет только на поле passed; нам важны сами similarity.
        using var req = new HttpRequestMessage(HttpMethod.Post, "compare?threshold=0");
        req.Content = form;

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("[ImageAnalyzer] Non-success status: {StatusCode}", (int)resp.StatusCode);
            return null;
        }

        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ImageAnalyzerCompareResponseDto>(s, JsonOpt, ct);
        if (dto is null)
            return null;

        return new ImageAnalyzerCompareResult(
            ClipSimilarity: dto.ClipSimilarity,
            PHashSimilarity: dto.PHashSimilarity,
            CombinedSimilarity: dto.CombinedSimilarity,
            Model: dto.Model ?? "",
            Device: dto.Device ?? "");
    }
}
