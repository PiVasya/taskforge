using System.Text.Json.Serialization;

namespace taskforge.Services.ImageTests;

internal sealed class ImageAnalyzerCompareResponseDto
{
    [JsonPropertyName("clip_similarity")]
    public double ClipSimilarity { get; set; }

    [JsonPropertyName("phash_similarity")]
    public double PHashSimilarity { get; set; }

    [JsonPropertyName("combined_similarity")]
    public double CombinedSimilarity { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("device")]
    public string? Device { get; set; }
}
