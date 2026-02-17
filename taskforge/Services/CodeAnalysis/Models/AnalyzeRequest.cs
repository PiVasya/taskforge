using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace taskforge.Services.CodeAnalysis.Models;

public sealed class AnalyzeRequest
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    // Optional task-specific forbids (not wired yet; kept for future DB integration)
    [JsonPropertyName("extra_forbidden")]
    public List<ForbiddenPattern>? ExtraForbidden { get; set; }
}
