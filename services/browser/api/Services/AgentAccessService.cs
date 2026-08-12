using System.Net;
using System.Text;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

public sealed class AgentAccessService(
    SiteRouteCatalog routeCatalog,
    BrowserUrlPolicy urlPolicy,
    BrowserOptions options)
{
    private readonly SiteRouteCatalog _routeCatalog = routeCatalog;
    private readonly BrowserUrlPolicy _urlPolicy = urlPolicy;
    private readonly BrowserOptions _options = options;

    public string BuildIndexHtml(HttpRequest request)
    {
        var root = PublicRoot(request);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
          .Append("<title>TaskForge AI / crawler access — TaskForge.by</title>")
          .Append("<meta name=\"robots\" content=\"index,follow\">")
          .Append("<link rel=\"canonical\" href=\"").Append(Html(root + "/ai-access")).Append("\">")
          .Append("<link rel=\"alternate\" type=\"application/json\" href=\"/.well-known/taskforge-ai.json\">")
          .Append("<link rel=\"alternate\" type=\"text/plain\" href=\"/llms.txt\">")
          .Append("<style>body{font:16px/1.5 system-ui,sans-serif;max-width:1050px;margin:40px auto;padding:0 20px;color:#111;background:#fff}code{background:#f4f4f4;padding:2px 5px;border-radius:4px}a{color:#0645ad}li{margin:.35rem 0}.muted{color:#666}.route{margin:1rem 0;padding:1rem;border:1px solid #ddd;border-radius:8px}.links a{margin-right:1rem}</style>")
          .Append("</head><body><main>")
          .Append("<h1>TaskForge AI / crawler access — TaskForge.by</h1>")
          .Append("<h2>Product summary</h2><p><strong>TaskForge.by</strong> is an educational platform for programming courses, study notes, coding and test assignments, automated solution checking, progress tracking and ratings.</p><p>This is the non-JavaScript entry point for automated clients. If an agent only knows <code>").Append(Html(root)).Append("</code>, it should discover this page from the root HTML and continue here.</p>")
          .Append("<p class=\"links\"><a href=\"/.well-known/taskforge-ai.json\">Discovery JSON</a><a href=\"/llms.txt\">llms.txt</a><a href=\"/api/site/info\">Site API info</a><a href=\"/api/site/routes\">Route catalog</a><a href=\"/api/site/agent/playbook\">Agent playbook</a><a href=\"/api/browser/openapi.json\">OpenAPI</a></p>")
          .Append("<h2>Visual captures that do not require query-string links</h2>")
          .Append("<p>Each capture link opens the real TaskForge page in Chromium and stores short-lived immutable snapshot JSON plus the authoritative PNG. Discovery links use viewport captures for low latency; request full-page or PDF renders only when they are actually needed.</p>")
          .Append("<p class=\"muted\">Public captures are anonymous and read-only. Authenticated/private pages are intentionally not persisted into public artifact URLs.</p>");

        foreach (var siteGroup in _routeCatalog.GetRoutes().GroupBy(route => route.Site, StringComparer.OrdinalIgnoreCase))
        {
            var site = siteGroup.Key;
            var origin = _urlPolicy.Sites.GetValueOrDefault(site)?.AbsoluteUri.TrimEnd('/') ?? site;
            sb.Append("<h2>").Append(Html(site.ToUpperInvariant())).Append(" - ").Append(Html(origin)).Append("</h2>");

            foreach (var route in siteGroup.Where(IsCrawlerLinkable))
            {
                var pathTail = EncodePathTail(route.Path);
                var mobile = $"/api/site/agent/capture/{Uri.EscapeDataString(site)}/390/844/viewport/{pathTail}";
                var desktop = $"/api/site/agent/capture/{Uri.EscapeDataString(site)}/1440/900/viewport/{pathTail}";
                sb.Append("<section class=\"route\"><strong>")
                  .Append(Html(route.Title)).Append("</strong> <code>").Append(Html(route.Path)).Append("</code>")
                  .Append("<div class=\"links\"><a href=\"").Append(Html(mobile)).Append("\">mobile 390x844</a>")
                  .Append("<a href=\"").Append(Html(desktop)).Append("\">desktop 1440x900</a></div>");
                if (!string.IsNullOrWhiteSpace(route.Notes)) sb.Append("<div class=\"muted\">").Append(Html(route.Notes)).Append("</div>");
                sb.Append("</section>");
            }
        }

        sb.Append("<h2>Interactive agents</h2>")
          .Append("<p>Clients that can issue POST requests can register an ordinary AI-marked user, create a Browser API session and use click/fill/scroll actions. Start with the <a href=\"/api/site/agent/playbook\">agent playbook</a>, then use <a href=\"/llms.txt\">llms.txt</a> and <a href=\"/api/browser/openapi.json\">OpenAPI</a> for the full contract.</p>")
          .Append("<p>Semantic snapshot v2.2 exposes stable test metadata. Prefer <code>questionId + answerOptionKey</code> (or explicit one-based indexes) instead of translated radio-button labels. For reliable serial solving, use the ordinary authenticated submit/attempt APIs after Browser API discovery; if a UI submit response is lost, reconcile with the authoritative GET before retrying.</p>")
          .Append("<p>An <code>accountType=ai</code> account remains an ordinary user with no extra role or hidden-data access. Operator resource policy may give AI accounts unlimited task energy and higher per-user throughput; <code>/api/me/quotas</code> is the authoritative quota view.</p>")
          .Append("<p class=\"muted\">Recommended expensive-capture concurrency: ").Append(_options.RecommendedCaptureConcurrency).Append(". Artifact TTL: ").Append(_options.AgentArtifactTtlSeconds).Append(" seconds.</p>")
          .Append("</main></body></html>");
        return sb.ToString();
    }

    public void EnrichDiscoveredLinks(SiteSnapshotResponse snapshot, string site)
    {
        snapshot.DiscoveredLinks = snapshot.Elements
            .Where(element => string.Equals(element.Role, "link", StringComparison.OrdinalIgnoreCase))
            .Where(element => IsFollowablePageHref(element.Href))
            .GroupBy(element => element.Href!, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(100)
            .Select(element =>
            {
                var href = element.Href!;
                var label = string.IsNullOrWhiteSpace(element.Name) ? href : element.Name;
                return new SnapshotDiscoveredLink
                {
                    Name = label,
                    SourcePath = href,
                    CaptureCurrentViewport = CapturePath(site, snapshot.Viewport.Width, snapshot.Viewport.Height, "viewport", href),
                    CaptureMobile = CapturePath(site, 390, 844, "viewport", href),
                    CaptureDesktop = CapturePath(site, 1440, 900, "viewport", href)
                };
            })
            .ToList();
    }

    public string BuildCaptureHtml(HttpRequest request, PublicAgentArtifactManifest manifest, SiteSnapshotResponse snapshot)
    {
        var root = PublicRoot(request);
        var baseUrl = $"{root}/ai-artifacts/{manifest.Id}";
        var png = $"{baseUrl}/render.png";
        var pdf = $"{baseUrl}/render.pdf";
        var snapshotUrl = $"{baseUrl}/snapshot.json";
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<meta name=\"robots\" content=\"noindex,follow,noarchive,nosnippet\">")
            .Append("<title>TaskForge capture - ").Append(Html(manifest.Title)).Append("</title></head><body><main>")
            .Append("<h1>TaskForge Chromium capture</h1>")
            .Append("<p><strong>Source:</strong> <a href=\"").Append(Html(manifest.SourceUrl)).Append("\">").Append(Html(manifest.SourceUrl)).Append("</a></p>")
            .Append("<p><strong>Captured:</strong> ").Append(manifest.CreatedAtUtc.ToString("O")).Append("; <strong>expires:</strong> ").Append(manifest.ExpiresAtUtc.ToString("O")).Append("</p>")
            .Append("<p><strong>Pixels:</strong> ").Append(manifest.Width).Append(" x ").Append(manifest.Height)
            .Append("; <strong>viewport:</strong> ").Append(snapshot.Viewport.Width).Append(" x ").Append(snapshot.Viewport.Height)
            .Append("; <strong>full page:</strong> ").Append(manifest.FullPage.ToString().ToLowerInvariant()).Append("</p>")
            .Append("<ul><li><a href=\"").Append(Html(snapshotUrl)).Append("\">semantic snapshot JSON</a></li>")
            .Append("<li><a href=\"").Append(Html(png)).Append("\">authoritative Chromium PNG</a></li>");
        if (manifest.HasPdf)
        {
            html.Append("<li><a href=\"").Append(Html(pdf)).Append("\">PDF compatibility wrapper</a></li>");
        }
        html.Append("<li><a href=\"").Append(Html(root + "/ai-access")).Append("\">back to TaskForge AI access</a></li></ul>")
            .Append(manifest.HasPdf
                ? "<p>Visual agents should prefer the PNG when their client can inspect images. PDF exists only as a compatibility wrapper.</p>"
                : "<p>Visual agents should use the PNG. Public crawler captures intentionally skip synchronous PDF generation for latency; use /api/site/render.pdf only when a PDF is required.</p>");

        if (snapshot.DiscoveredLinks.Count > 0)
        {
            var siteOrigin = _urlPolicy.Sites.GetValueOrDefault(manifest.Site)?.AbsoluteUri.TrimEnd('/') ?? string.Empty;
            html.Append("<h2>Links discovered on this rendered page</h2><ul>");
            foreach (var link in snapshot.DiscoveredLinks)
            {
                var sourceUrl = siteOrigin + link.SourcePath;
                html.Append("<li><strong>").Append(Html(link.Name)).Append("</strong> <code>").Append(Html(link.SourcePath)).Append("</code>")
                    .Append("<div class=\"links\"><a href=\"").Append(Html(sourceUrl)).Append("\">source page</a>")
                    .Append("<a href=\"").Append(Html(link.CaptureCurrentViewport)).Append("\">capture current viewport</a>")
                    .Append("<a href=\"").Append(Html(link.CaptureMobile)).Append("\">capture mobile</a>")
                    .Append("<a href=\"").Append(Html(link.CaptureDesktop)).Append("\">capture desktop</a></div></li>");
            }
            html.Append("</ul>");
        }

        html.Append("<img src=\"").Append(Html(png)).Append("\" alt=\"TaskForge Chromium capture\" style=\"display:block;max-width:100%;height:auto;border:1px solid #ddd\">")
            .Append("</main></body></html>");
        return html.ToString();
    }

    public string BuildSitemapXml(HttpRequest request)
    {
        var root = PublicRoot(request);
        var indexablePaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "/",
            "/news",
            "/privacy"
        };
        var urls = new List<string> { root + "/ai-access" };
        foreach (var route in _routeCatalog.GetRoutes("main").Where(IsCrawlerLinkable))
        {
            if (!indexablePaths.Contains(route.Path)) continue;
            urls.Add(root + route.Path);
        }

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n"
               + string.Join("\n", urls.Distinct(StringComparer.Ordinal).Select(url => $"  <url><loc>{WebUtility.HtmlEncode(url)}</loc></url>"))
               + "\n</urlset>\n";
    }

    private static bool IsCrawlerLinkable(SiteRouteDto route)
        => !route.RequiresAuthentication
           && !route.Path.Contains(':')
           && !string.Equals(route.Kind, "redirect", StringComparison.OrdinalIgnoreCase);

    private static bool IsFollowablePageHref(string? href)
        => !string.IsNullOrWhiteSpace(href)
           && href.StartsWith('/')
           && !href.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
           && !href.StartsWith("/ai-artifacts/", StringComparison.OrdinalIgnoreCase)
           && !href.Contains("[query-redacted]", StringComparison.Ordinal)
           && !href.Contains("[fragment-redacted]", StringComparison.Ordinal);

    private static string CapturePath(string site, int width, int height, string mode, string path)
        => $"/api/site/agent/capture/{Uri.EscapeDataString(site)}/{width}/{height}/{mode}/{EncodePathTail(path)}";

    private static string EncodePathTail(string path)
    {
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0) return string.Empty;
        return string.Join('/', trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string PublicRoot(HttpRequest request)
    {
        var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
        var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.Value;
        return $"{scheme}://{host}".TrimEnd('/');
    }
}
