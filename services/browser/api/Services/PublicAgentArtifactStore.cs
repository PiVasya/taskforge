using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

public sealed record PublicAgentArtifactManifest(
    string Id,
    string Site,
    string SourceUrl,
    string Title,
    int Width,
    int Height,
    bool FullPage,
    bool FullPageTruncated,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool HasPdf = true);

public sealed record PublicAgentArtifactBundle(
    PublicAgentArtifactManifest Manifest,
    byte[] SnapshotJson,
    byte[] Png,
    byte[]? Pdf);

public sealed class PublicAgentArtifactStore(
    IDistributedCache cache,
    BrowserOptions options,
    ILogger<PublicAgentArtifactStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache = cache;
    private readonly BrowserOptions _options = options;
    private readonly ILogger<PublicAgentArtifactStore> _logger = logger;

    public async Task<PublicAgentArtifactManifest> CreateAsync(
        SiteSnapshotResponse snapshot,
        RenderArtifact png,
        RenderArtifact? pdf,
        CancellationToken cancellationToken)
    {
        var snapshotJson = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        EnsurePersistable("snapshot", snapshotJson.Length);
        EnsurePersistable("PNG", png.Bytes.Length);
        if (pdf is not null) EnsurePersistable("PDF", pdf.Bytes.Length);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(snapshotJson);
        hasher.AppendData(png.Bytes);
        if (pdf is not null) hasher.AppendData(pdf.Bytes);
        var id = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var ttl = TimeSpan.FromSeconds(_options.AgentArtifactTtlSeconds);
        var manifest = new PublicAgentArtifactManifest(
            id,
            snapshot.Site,
            snapshot.Url,
            snapshot.Title,
            png.Width,
            png.Height,
            png.FullPage,
            png.FullPageTruncated,
            now,
            now.Add(ttl),
            pdf is not null);

        var entryOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl };
        try
        {
            await _cache.SetAsync(Key(id, "manifest"), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions), entryOptions, cancellationToken);
            await _cache.SetAsync(Key(id, "snapshot"), snapshotJson, entryOptions, cancellationToken);
            await _cache.SetAsync(Key(id, "png"), png.Bytes, entryOptions, cancellationToken);
            if (pdf is not null)
            {
                await _cache.SetAsync(Key(id, "pdf"), pdf.Bytes, entryOptions, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist public agent artifact {ArtifactId}.", id);
            throw new BrowserApiException(
                StatusCodes.Status503ServiceUnavailable,
                "AGENT_ARTIFACT_STORE_UNAVAILABLE",
                "Не удалось сохранить временный публичный артефакт Browser API. Повторите попытку позже.");
        }

        return manifest;
    }

    public async Task<PublicAgentArtifactBundle?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidId(id)) return null;

        try
        {
            var manifestBytes = await _cache.GetAsync(Key(id, "manifest"), cancellationToken);
            if (manifestBytes is null) return null;
            var manifest = JsonSerializer.Deserialize<PublicAgentArtifactManifest>(manifestBytes, JsonOptions);
            if (manifest is null || manifest.ExpiresAtUtc <= DateTimeOffset.UtcNow) return null;

            var snapshot = await _cache.GetAsync(Key(id, "snapshot"), cancellationToken);
            var png = await _cache.GetAsync(Key(id, "png"), cancellationToken);
            var pdf = manifest.HasPdf
                ? await _cache.GetAsync(Key(id, "pdf"), cancellationToken)
                : null;
            if (snapshot is null || png is null || (manifest.HasPdf && pdf is null)) return null;

            return new PublicAgentArtifactBundle(manifest, snapshot, png, pdf);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read public agent artifact {ArtifactId}.", id);
            return null;
        }
    }

    public static bool IsValidId(string? id)
        => id is { Length: 64 } && id.All(static c => char.IsAsciiHexDigit(c));

    private void EnsurePersistable(string kind, int bytes)
    {
        if (bytes <= 0 || bytes > _options.MaxCachedArtifactBytes)
        {
            throw new BrowserApiException(
                StatusCodes.Status413PayloadTooLarge,
                "AGENT_ARTIFACT_TOO_LARGE",
                $"{kind} артефакт нельзя сохранить для crawler-доступа: размер {bytes} байт превышает предел {_options.MaxCachedArtifactBytes}. Уменьшите viewport или используйте viewport-режим вместо full-page.");
        }
    }

    private static string Key(string id, string part) => $"tf:browser:agent-artifact:{id}:{part}";
}
