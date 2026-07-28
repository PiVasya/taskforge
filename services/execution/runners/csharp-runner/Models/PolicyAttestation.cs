namespace Runner.Models;

public sealed class PolicyAttestation
{
    public required string Schema { get; init; }
    public required string Language { get; init; }
    public required string Profile { get; init; }
    public required string SourceSha256 { get; init; }
    public required string PolicyVersion { get; init; }
    public long IssuedAtUnix { get; init; }
    public long ExpiresAtUnix { get; init; }
    public required string Nonce { get; init; }
    public required string SignatureB64 { get; init; }
}
