using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace taskforge.Services.CodeAnalysis.Models;

public sealed class AnalyzeResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("errors")]
    public List<Violation> Errors { get; set; } = new();

    [JsonPropertyName("hits")]
    public List<Hit> Hits { get; set; } = new();
}

public sealed class Violation
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("pattern_id")]
    public string? PatternId { get; set; }
}

public sealed class Hit
{
    [JsonPropertyName("pattern_id")]
    public string? PatternId { get; set; }

    [JsonPropertyName("needle")]
    public string Needle { get; set; } = "";

    [JsonPropertyName("position")]
    public int Position { get; set; }

    [JsonPropertyName("preview")]
    public string Preview { get; set; } = "";
}

public sealed class ForbiddenPattern
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("needle")]
    public string Needle { get; set; } = "";

    [JsonPropertyName("case_sensitive")]
    public bool? CaseSensitive { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
