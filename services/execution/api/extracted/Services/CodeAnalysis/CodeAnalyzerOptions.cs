namespace taskforge.Services.CodeAnalysis;

public sealed class CodeAnalyzerOptions
{
    public bool Enabled { get; set; } = false;
    public string Url { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 6;
}
