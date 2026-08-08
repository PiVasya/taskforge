using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;
using TaskForge.Browser.Api.Infrastructure;

namespace TaskForge.Browser.Api.Services;

public sealed record RenderArtifact(
    byte[] Bytes,
    string ContentType,
    int Width,
    int Height,
    bool FullPage,
    bool FullPageTruncated,
    bool Annotated,
    bool CacheHit);

public sealed record AgentCaptureBundle(
    SiteSnapshotResponse Snapshot,
    RenderArtifact Png,
    RenderArtifact Pdf);

public sealed class SiteInspectionService(
    BrowserPageFactory pageFactory,
    SnapshotBuilder snapshotBuilder,
    BrowserScreenshotService screenshots,
    BrowserCapacityGate capacityGate,
    BrowserResponseCache cache,
    BrowserOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly BrowserPageFactory _pageFactory = pageFactory;
    private readonly SnapshotBuilder _snapshotBuilder = snapshotBuilder;
    private readonly BrowserScreenshotService _screenshots = screenshots;
    private readonly BrowserCapacityGate _capacityGate = capacityGate;
    private readonly BrowserResponseCache _cache = cache;
    private readonly BrowserOptions _options = options;

    public async Task<(SiteSnapshotResponse Snapshot, bool CacheHit)> SnapshotAsync(
        string? site,
        string? path,
        int width,
        int height,
        int waitMilliseconds,
        bool includeText,
        BrowserCaller caller,
        CancellationToken cancellationToken)
    {
        _pageFactory.ValidateViewport(width, height);
        var cacheKey = CacheKey("snapshot", site, path, width, height, waitMilliseconds, includeText, caller, fullPage: false, annotated: false);
        if (!caller.IsAuthenticated)
        {
            var cached = await _cache.GetAsync(cacheKey, cancellationToken);
            if (cached is not null)
            {
                var snapshot = JsonSerializer.Deserialize<SiteSnapshotResponse>(cached, JsonOptions);
                if (snapshot is not null) return (snapshot, true);
            }
        }

        await using var lease = await _capacityGate.EnterAsync(cancellationToken);
        await using var handle = await _pageFactory.CreateAsync(site, width, height, readOnly: true, caller, cancellationToken);
        await _pageFactory.NavigateAsync(handle, path, waitMilliseconds, cancellationToken);
        var result = await _snapshotBuilder.BuildAsync(handle, includeText, cancellationToken);

        if (!caller.IsAuthenticated)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
            await _cache.SetAsync(cacheKey, bytes, TimeSpan.FromSeconds(_options.PublicCacheSeconds), _options.MaxCachedArtifactBytes, cancellationToken);
        }

        return (result, false);
    }

    public async Task<RenderArtifact> RenderPngAsync(
        string? site,
        string? path,
        int width,
        int height,
        int waitMilliseconds,
        bool fullPage,
        bool annotated,
        BrowserCaller caller,
        CancellationToken cancellationToken)
    {
        _pageFactory.ValidateViewport(width, height);
        var cacheKey = CacheKey("png", site, path, width, height, waitMilliseconds, includeText: false, caller, fullPage, annotated);
        if (!caller.IsAuthenticated)
        {
            var cached = await _cache.GetAsync(cacheKey, cancellationToken);
            if (BrowserArtifactCacheCodec.TryDecode(cached, out var artifact))
            {
                return new RenderArtifact(
                    artifact.Bytes,
                    "image/png",
                    artifact.Width,
                    artifact.Height,
                    artifact.FullPage,
                    artifact.FullPageTruncated,
                    artifact.Annotated,
                    true);
            }
        }

        await using var lease = await _capacityGate.EnterAsync(cancellationToken);
        await using var handle = await _pageFactory.CreateAsync(site, width, height, readOnly: true, caller, cancellationToken);
        await _pageFactory.NavigateAsync(handle, path, waitMilliseconds, cancellationToken);
        var capture = await _screenshots.CaptureAsync(handle, fullPage, annotated, cancellationToken);

        if (!caller.IsAuthenticated)
        {
            var cached = BrowserArtifactCacheCodec.Encode(new CachedBrowserArtifact(
                capture.Bytes,
                capture.Width,
                capture.Height,
                capture.FullPage,
                capture.FullPageTruncated,
                capture.Annotated));
            await _cache.SetAsync(cacheKey, cached, TimeSpan.FromSeconds(_options.PublicCacheSeconds), _options.MaxCachedArtifactBytes, cancellationToken);
        }

        return new RenderArtifact(
            capture.Bytes,
            "image/png",
            capture.Width,
            capture.Height,
            capture.FullPage,
            capture.FullPageTruncated,
            capture.Annotated,
            false);
    }

    public async Task<RenderArtifact> RenderPdfAsync(
        string? site,
        string? path,
        int width,
        int height,
        int waitMilliseconds,
        bool fullPage,
        bool annotated,
        BrowserCaller caller,
        CancellationToken cancellationToken)
    {
        _pageFactory.ValidateViewport(width, height);
        var cacheKey = CacheKey("pdf", site, path, width, height, waitMilliseconds, includeText: false, caller, fullPage, annotated);
        if (!caller.IsAuthenticated)
        {
            var cached = await _cache.GetAsync(cacheKey, cancellationToken);
            if (BrowserArtifactCacheCodec.TryDecode(cached, out var artifact))
            {
                return new RenderArtifact(
                    artifact.Bytes,
                    "application/pdf",
                    artifact.Width,
                    artifact.Height,
                    artifact.FullPage,
                    artifact.FullPageTruncated,
                    artifact.Annotated,
                    true);
            }
        }

        await using var lease = await _capacityGate.EnterAsync(cancellationToken);
        await using var handle = await _pageFactory.CreateAsync(site, width, height, readOnly: true, caller, cancellationToken);
        await _pageFactory.NavigateAsync(handle, path, waitMilliseconds, cancellationToken);
        var capture = await _screenshots.CaptureAsync(handle, fullPage, annotated, cancellationToken);

        var pdf = await BuildPdfFromCaptureAsync(handle.Page, capture, cancellationToken);

        if (!caller.IsAuthenticated)
        {
            var cached = BrowserArtifactCacheCodec.Encode(new CachedBrowserArtifact(
                pdf,
                capture.Width,
                capture.Height,
                capture.FullPage,
                capture.FullPageTruncated,
                capture.Annotated));
            await _cache.SetAsync(cacheKey, cached, TimeSpan.FromSeconds(_options.PublicCacheSeconds), _options.MaxCachedArtifactBytes, cancellationToken);
        }

        return new RenderArtifact(
            pdf,
            "application/pdf",
            capture.Width,
            capture.Height,
            capture.FullPage,
            capture.FullPageTruncated,
            capture.Annotated,
            false);
    }

    public async Task<AgentCaptureBundle> CaptureAgentBundleAsync(
        string? site,
        string? path,
        int width,
        int height,
        int waitMilliseconds,
        bool fullPage,
        CancellationToken cancellationToken)
    {
        _pageFactory.ValidateViewport(width, height);
        var anonymousCaller = new BrowserCaller(false, null, "anonymous", null, "agent-public-capture", "agent-public-capture", false);

        await using var lease = await _capacityGate.EnterAsync(cancellationToken);
        await using var handle = await _pageFactory.CreateAsync(site, width, height, readOnly: true, anonymousCaller, cancellationToken);
        await _pageFactory.NavigateAsync(handle, path, waitMilliseconds, cancellationToken);

        var snapshot = await _snapshotBuilder.BuildAsync(handle, includeText: true, cancellationToken);
        var capture = await _screenshots.CaptureAsync(handle, fullPage, annotated: false, cancellationToken);
        var pdfBytes = await BuildPdfFromCaptureAsync(handle.Page, capture, cancellationToken);

        var png = new RenderArtifact(
            capture.Bytes,
            "image/png",
            capture.Width,
            capture.Height,
            capture.FullPage,
            capture.FullPageTruncated,
            capture.Annotated,
            false);

        var pdf = new RenderArtifact(
            pdfBytes,
            "application/pdf",
            capture.Width,
            capture.Height,
            capture.FullPage,
            capture.FullPageTruncated,
            capture.Annotated,
            false);

        return new AgentCaptureBundle(snapshot, png, pdf);
    }

    private async Task<byte[]> BuildPdfFromCaptureAsync(
        IPage page,
        BrowserScreenshot capture,
        CancellationToken cancellationToken)
    {
        // PDF is a compatibility wrapper around the authoritative Chromium PNG.
        // Use explicit PDF points (72 dpi) instead of CSS px (96 dpi) to preserve
        // the screenshot aspect ratio across PDF viewers and avoid subtle stretching.
        var widthPoints = capture.Width * 72d / 96d;
        var heightPoints = capture.Height * 72d / 96d;
        var width = widthPoints.ToString("0.###", CultureInfo.InvariantCulture);
        var height = heightPoints.ToString("0.###", CultureInfo.InvariantCulture);
        var base64 = Convert.ToBase64String(capture.Bytes);

        await page.SetContentAsync(
            $"<!doctype html><html><head><meta charset=\"utf-8\"><style>@page{{margin:0;size:{width}pt {height}pt}}html,body{{margin:0;padding:0;width:{width}pt;height:{height}pt;overflow:hidden;background:#000}}img{{display:block;width:100%;height:100%;object-fit:contain}}</style></head><body><img alt=\"TaskForge browser render\" src=\"data:image/png;base64,{base64}\"></body></html>",
            new PageSetContentOptions { WaitUntil = WaitUntilState.Load }).WaitAsync(cancellationToken);

        var pdf = await page.PdfAsync(new PagePdfOptions
        {
            PrintBackground = true,
            Width = $"{width}pt",
            Height = $"{height}pt",
            Margin = new Margin { Top = "0", Right = "0", Bottom = "0", Left = "0" },
            PreferCSSPageSize = true
        }).WaitAsync(cancellationToken);

        _screenshots.EnsureArtifactSize(pdf.Length);
        return pdf;
    }

    private string CacheKey(
        string kind,
        string? site,
        string? path,
        int width,
        int height,
        int wait,
        bool includeText,
        BrowserCaller caller,
        bool fullPage,
        bool annotated)
    {
        var raw = string.Join('|', new[]
        {
            _options.DeploymentVersion,
            kind,
            site ?? _options.DefaultSite,
            path ?? "/",
            width.ToString(),
            height.ToString(),
            wait.ToString(),
            includeText.ToString(),
            fullPage.ToString(),
            annotated.ToString(),
            caller.IsAuthenticated ? caller.OwnerKey : "anonymous"
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return $"tf:browser:artifact:{hash}";
    }
}
