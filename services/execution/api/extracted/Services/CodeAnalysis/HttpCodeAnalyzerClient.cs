using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using taskforge.Services.CodeAnalysis.Models;

namespace taskforge.Services.CodeAnalysis;

public sealed class HttpCodeAnalyzerClient : ICodeAnalyzerClient
{
    private readonly HttpClient _http;

    public HttpCodeAnalyzerClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<AnalyzeResponse> AnalyzeAsync(AnalyzeRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("analyze", req, ct);
        resp.EnsureSuccessStatusCode();

        var data = await resp.Content.ReadFromJsonAsync<AnalyzeResponse>(cancellationToken: ct);
        return data ?? new AnalyzeResponse { Ok = true };
    }
}
