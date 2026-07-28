namespace Runner.Models;
public class RunRequest
{
    public required string Code { get; init; }
    public string? Input { get; init; }
    public int? TimeLimitMs { get; init; }
    public int? MemoryLimitMb { get; init; }
    public PolicyAttestation? Attestation { get; init; }
}
