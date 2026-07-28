using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Runner.Models;

namespace Runner.Services;

public sealed partial class PolicyAttestationVerifier : IDisposable
{
    private const string Schema = "taskforge-code-policy-attestation-v2";
    private const string DefaultPolicyVersion = "2026-07-28.4";
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(30);
    private readonly RSA _rsa;
    private readonly string _policyVersion;

    public PolicyAttestationVerifier()
    {
        var path = Environment.GetEnvironmentVariable("CODE_ANALYZER_PUBLIC_KEY_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = "/run/secrets/code-analyzer-public.pem";
        }
        var pem = File.ReadAllText(path);
        _rsa = RSA.Create();
        _rsa.ImportFromPem(pem);
        if (_rsa.KeySize < 3072)
        {
            _rsa.Dispose();
            throw new InvalidOperationException("Code analyzer RSA key is too small.");
        }
        _policyVersion = Environment.GetEnvironmentVariable("CODE_ANALYZER_POLICY_VERSION")?.Trim();
        if (string.IsNullOrWhiteSpace(_policyVersion))
        {
            _policyVersion = DefaultPolicyVersion;
        }
    }

    public string? Verify(string language, string profile, string source, PolicyAttestation? attestation)
    {
        if (attestation is null) return "Missing code analyzer attestation.";
        if (!string.Equals(attestation.Schema, Schema, StringComparison.Ordinal)) return "Unsupported code analyzer attestation schema.";
        if (!string.Equals(attestation.Language, language, StringComparison.Ordinal)
            || !string.Equals(attestation.Profile, profile, StringComparison.Ordinal))
        {
            return "Code analyzer attestation target mismatch.";
        }
        if (!string.Equals(attestation.PolicyVersion, _policyVersion, StringComparison.Ordinal))
        {
            return "Code analyzer policy version mismatch.";
        }
        if (attestation.IssuedAtUnix <= 0 || attestation.ExpiresAtUnix <= attestation.IssuedAtUnix)
        {
            return "Invalid code analyzer attestation lifetime.";
        }
        var issued = DateTimeOffset.FromUnixTimeSeconds(attestation.IssuedAtUnix);
        var expires = DateTimeOffset.FromUnixTimeSeconds(attestation.ExpiresAtUnix);
        var now = DateTimeOffset.UtcNow;
        if (issued > now + MaximumClockSkew) return "Code analyzer attestation is from the future.";
        if (expires < now) return "Code analyzer attestation expired.";
        if (expires - issued > MaximumLifetime) return "Code analyzer attestation lifetime is too long.";
        if (!NonceRegex().IsMatch(attestation.Nonce)) return "Invalid code analyzer attestation nonce.";

        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        if (!string.Equals(sourceHash, attestation.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            return "Code changed after analyzer approval.";
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(attestation.SignatureB64);
        }
        catch (FormatException)
        {
            return "Invalid code analyzer signature encoding.";
        }
        if (signature.Length == 0) return "Invalid code analyzer signature encoding.";

        var canonical = string.Join(
            "\n",
            Schema,
            attestation.Language,
            attestation.Profile,
            attestation.SourceSha256.ToLowerInvariant(),
            attestation.PolicyVersion,
            attestation.IssuedAtUnix.ToString(CultureInfo.InvariantCulture),
            attestation.ExpiresAtUnix.ToString(CultureInfo.InvariantCulture),
            attestation.Nonce,
            string.Empty);
        var valid = _rsa.VerifyData(
            Encoding.UTF8.GetBytes(canonical),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return valid ? null : "Invalid code analyzer signature.";
    }

    public void Dispose() => _rsa.Dispose();

    [GeneratedRegex("^[0-9a-f]{32,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex NonceRegex();
}
