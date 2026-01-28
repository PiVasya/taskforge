namespace taskforge.Services.ImageTests;

public sealed record ImageAnalyzerCompareResult(
    double ClipSimilarity,
    double PHashSimilarity,
    double CombinedSimilarity,
    string Model,
    string Device);
