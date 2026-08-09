using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

public sealed record PublicAgentCaptureResult(
    PublicAgentArtifactManifest Manifest,
    SiteSnapshotResponse Snapshot,
    bool CacheHit);

public sealed class PublicAgentCaptureService(
    SiteInspectionService inspections,
    PublicAgentArtifactStore artifacts,
    AgentAccessService access,
    BrowserUrlPolicy urlPolicy,
    BrowserOptions options,
    IDistributedCache cache,
    ILogger<PublicAgentCaptureService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<string, Lazy<Task<PublicAgentCaptureResult>>> _flights = new(StringComparer.Ordinal);
    private readonly SiteInspectionService _inspections = inspections;
    private readonly PublicAgentArtifactStore _artifacts = artifacts;
    private readonly AgentAccessService _access = access;
    private readonly BrowserUrlPolicy _urlPolicy = urlPolicy;
    private readonly BrowserOptions _options = options;
    private readonly IDistributedCache _cache = cache;
    private readonly ILogger<PublicAgentCaptureService> _logger = logger;

    public async Task<PublicAgentCaptureResult> CaptureAsync(
        string site,
        string path,
        int width,
        int height,
        bool fullPage,
        CancellationToken cancellationToken)
    {
        var (siteKey, siteBase) = _urlPolicy.ResolveSite(site);
        var target = _urlPolicy.BuildPageUri(siteBase, path);
        var normalizedPath = target.PathAndQuery;
        var key = CaptureKey(siteKey, normalizedPath, width, height, fullPage);

        var cached = await TryReadCachedAsync(key, cancellationToken);
        if (cached is not null) return cached;

        var flight = _flights.GetOrAdd(
            key,
            _ => new Lazy<Task<PublicAgentCaptureResult>>(
                () => CaptureAndReleaseAsync(key, siteKey, normalizedPath, width, height, fullPage),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return await flight.Value.WaitAsync(cancellationToken);
    }

    private async Task<PublicAgentCaptureResult> CaptureAndReleaseAsync(
        string key,
        string site,
        string path,
        int width,
        int height,
        bool fullPage)
    {
        var stage = "cache-recheck";
        var started = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(System.Math.Clamp(_options.CaptureTimeoutSeconds, 10, 180)));

        try
        {
            _logger.LogInformation(
                "Public agent capture started. site={Site} path={Path} viewport={Width}x{Height} fullPage={FullPage}",
                site, path, width, height, fullPage);

            var secondCacheCheck = await TryReadCachedAsync(key, timeout.Token);
            if (secondCacheCheck is not null) return secondCacheCheck;

            stage = "chromium-capture";
            var bundle = await _inspections.CaptureAgentBundleAsync(
                site,
                path,
                width,
                height,
                _options.AgentCaptureWaitMilliseconds,
                fullPage,
                timeout.Token);

            stage = "link-enrichment";
            _access.EnrichDiscoveredLinks(bundle.Snapshot, site, fullPage);

            stage = "artifact-persist";
            var manifest = await _artifacts.CreateAsync(bundle.Snapshot, bundle.Png, bundle.Pdf, timeout.Token);

            stage = "cache-pointer";
            await WritePointerAsync(key, manifest.Id, timeout.Token);
            _logger.LogInformation(
                "Public agent capture completed. site={Site} path={Path} viewport={Width}x{Height} fullPage={FullPage} elapsedMs={ElapsedMilliseconds} artifact={ArtifactId} pdf={HasPdf}",
                site, path, width, height, fullPage, started.ElapsedMilliseconds, manifest.Id, manifest.HasPdf);
            return new PublicAgentCaptureResult(manifest, bundle.Snapshot, false);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Public agent capture timed out. site={Site} path={Path} viewport={Width}x{Height} fullPage={FullPage} stage={Stage} elapsedMs={ElapsedMilliseconds}",
                site, path, width, height, fullPage, stage, started.ElapsedMilliseconds);
            throw new BrowserApiException(
                StatusCodes.Status504GatewayTimeout,
                "AGENT_CAPTURE_TIMEOUT",
                "Публичный Chromium capture не завершился в установленный срок.",
                details: new
                {
                    stage,
                    timeoutSeconds = _options.CaptureTimeoutSeconds,
                    elapsedMilliseconds = started.ElapsedMilliseconds,
                    site,
                    path,
                    width,
                    height,
                    fullPage
                },
                innerException: ex);
        }
        finally
        {
            _flights.TryRemove(key, out _);
        }
    }

    private async Task<PublicAgentCaptureResult?> TryReadCachedAsync(string key, CancellationToken cancellationToken)
    {
        if (_options.CaptureCacheSeconds <= 0) return null;
        try
        {
            var artifactId = await _cache.GetStringAsync(PointerKey(key), cancellationToken);
            if (string.IsNullOrWhiteSpace(artifactId)) return null;
            var bundle = await _artifacts.GetAsync(artifactId, cancellationToken);
            if (bundle is null) return null;
            var snapshot = JsonSerializer.Deserialize<SiteSnapshotResponse>(bundle.SnapshotJson, JsonOptions);
            return snapshot is null ? null : new PublicAgentCaptureResult(bundle.Manifest, snapshot, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Public capture pointer cache was unavailable for {CaptureKey}.", key);
            return null;
        }
    }

    private async Task WritePointerAsync(string key, string artifactId, CancellationToken cancellationToken)
    {
        if (_options.CaptureCacheSeconds <= 0) return;
        try
        {
            await _cache.SetStringAsync(
                PointerKey(key),
                artifactId,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(
                        System.Math.Min(_options.CaptureCacheSeconds, _options.AgentArtifactTtlSeconds))
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to write public capture pointer cache for {CaptureKey}.", key);
        }
    }

    private string CaptureKey(string site, string path, int width, int height, bool fullPage)
    {
        var raw = string.Join('|', _options.DeploymentVersion, site, path, width, height, fullPage, _options.AgentCaptureWaitMilliseconds);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static string PointerKey(string key) => $"tf:browser:agent-capture:{key}";
}
