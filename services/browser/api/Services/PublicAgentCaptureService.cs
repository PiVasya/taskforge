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

public sealed class PublicAgentCaptureService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<string, CaptureFlight> _flights = new(StringComparer.Ordinal);
    private readonly SiteInspectionService _inspections;
    private readonly PublicAgentArtifactStore _artifacts;
    private readonly AgentAccessService _access;
    private readonly BrowserUrlPolicy _urlPolicy;
    private readonly BrowserOptions _options;
    private readonly IDistributedCache _cache;
    private readonly ILogger<PublicAgentCaptureService> _logger;
    private readonly SemaphoreSlim _publicCaptureSlots;

    public PublicAgentCaptureService(
        SiteInspectionService inspections,
        PublicAgentArtifactStore artifacts,
        AgentAccessService access,
        BrowserUrlPolicy urlPolicy,
        BrowserOptions options,
        IDistributedCache cache,
        ILogger<PublicAgentCaptureService> logger)
    {
        _inspections = inspections;
        _artifacts = artifacts;
        _access = access;
        _urlPolicy = urlPolicy;
        _options = options;
        _cache = cache;
        _logger = logger;
        var capacity = System.Math.Clamp(options.MaxConcurrentPublicCaptures, 1, 16);
        _publicCaptureSlots = new SemaphoreSlim(capacity, capacity);
    }

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

        CaptureFlight flight;
        while (true)
        {
            flight = _flights.GetOrAdd(
                key,
                _ => CreateFlight(key, siteKey, normalizedPath, width, height, fullPage));

            if (flight.TryAddWaiter()) break;
            RemoveFlight(key, flight);
        }

        try
        {
            return await flight.Work.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (flight.ReleaseWaiter())
            {
                RemoveFlight(key, flight);
                flight.Cancel();
                _logger.LogInformation(
                    "Public agent capture abandoned because all callers disconnected. site={Site} path={Path} viewport={Width}x{Height} fullPage={FullPage}",
                    siteKey, normalizedPath, width, height, fullPage);
            }
        }
    }

    private CaptureFlight CreateFlight(
        string key,
        string site,
        string path,
        int width,
        int height,
        bool fullPage)
    {
        CaptureFlight? flight = null;
        flight = new CaptureFlight(() => CaptureAndReleaseAsync(key, site, path, width, height, fullPage, flight!));
        return flight;
    }

    private async Task<PublicAgentCaptureResult> CaptureAndReleaseAsync(
        string key,
        string site,
        string path,
        int width,
        int height,
        bool fullPage,
        CaptureFlight flight)
    {
        var stage = "cache-recheck";
        var started = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(System.Math.Clamp(_options.CaptureTimeoutSeconds, 10, 180)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, flight.CancellationToken);
        var captureSlotHeld = false;

        try
        {
            _logger.LogInformation(
                "Public agent capture started. site={Site} path={Path} viewport={Width}x{Height} fullPage={FullPage}",
                site, path, width, height, fullPage);

            var secondCacheCheck = await TryReadCachedAsync(key, linked.Token);
            if (secondCacheCheck is not null) return secondCacheCheck;

            stage = "public-capacity";
            var queueWait = TimeSpan.FromMilliseconds(System.Math.Clamp(_options.PublicCaptureQueueWaitMilliseconds, 0, 5000));
            if (!await _publicCaptureSlots.WaitAsync(queueWait, linked.Token))
            {
                throw new BrowserApiException(
                    StatusCodes.Status429TooManyRequests,
                    "AGENT_CAPTURE_CAPACITY",
                    "Слишком много одновременных публичных Chromium capture. Повторите запрос последовательно.",
                    retryAfterSeconds: 1,
                    details: new
                    {
                        maxConcurrentPublicCaptures = _options.MaxConcurrentPublicCaptures,
                        recommendedCaptureConcurrency = _options.RecommendedCaptureConcurrency,
                        queueWaitMilliseconds = _options.PublicCaptureQueueWaitMilliseconds
                    });
            }
            captureSlotHeld = true;

            stage = "chromium-capture";
            var bundle = await _inspections.CaptureAgentBundleAsync(
                site,
                path,
                width,
                height,
                _options.AgentCaptureWaitMilliseconds,
                fullPage,
                linked.Token);

            stage = "link-enrichment";
            _access.EnrichDiscoveredLinks(bundle.Snapshot, site);

            stage = "artifact-persist";
            var manifest = await _artifacts.CreateAsync(bundle.Snapshot, bundle.Png, bundle.Pdf, linked.Token);

            stage = "cache-pointer";
            await WritePointerAsync(key, manifest.Id, linked.Token);
            _logger.LogInformation(
                "Public agent capture completed. site={Site} path={Path} viewport={Width}x{Height} fullPage={FullPage} elapsedMs={ElapsedMilliseconds} artifact={ArtifactId} pdf={HasPdf}",
                site, path, width, height, fullPage, started.ElapsedMilliseconds, manifest.Id, manifest.HasPdf);
            return new PublicAgentCaptureResult(manifest, bundle.Snapshot, false);
        }
        catch (OperationCanceledException) when (flight.CancellationToken.IsCancellationRequested && !timeout.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Public agent capture canceled after all callers disconnected. site={Site} path={Path} viewport={Width}x{Height} fullPage={FullPage} stage={Stage} elapsedMs={ElapsedMilliseconds}",
                site, path, width, height, fullPage, stage, started.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
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
            if (captureSlotHeld) _publicCaptureSlots.Release();
            RemoveFlight(key, flight);
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
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not BrowserApiException)
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

    private bool RemoveFlight(string key, CaptureFlight flight)
        => ((ICollection<KeyValuePair<string, CaptureFlight>>)_flights).Remove(new KeyValuePair<string, CaptureFlight>(key, flight));

    private static string PointerKey(string key) => $"tf:browser:agent-capture:{key}";

    public void Dispose() => _publicCaptureSlots.Dispose();

    private sealed class CaptureFlight
    {
        private readonly object _gate = new();
        private int _waiters;
        private bool _abandoned;
        private readonly CancellationTokenSource _cancellation = new();

        public CaptureFlight(Func<Task<PublicAgentCaptureResult>> factory)
        {
            Work = new Lazy<Task<PublicAgentCaptureResult>>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Lazy<Task<PublicAgentCaptureResult>> Work { get; }
        public CancellationToken CancellationToken => _cancellation.Token;

        public bool TryAddWaiter()
        {
            lock (_gate)
            {
                if (_abandoned) return false;
                _waiters++;
                return true;
            }
        }

        public bool ReleaseWaiter()
        {
            lock (_gate)
            {
                if (_waiters > 0) _waiters--;
                if (_waiters != 0 || _abandoned) return false;
                if (!Work.IsValueCreated || Work.Value.IsCompleted) return false;
                _abandoned = true;
                return true;
            }
        }

        public void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
