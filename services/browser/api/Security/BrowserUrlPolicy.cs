using TaskForge.Browser.Api.Configuration;

namespace TaskForge.Browser.Api.Security;

public sealed class BrowserUrlPolicy
{
    private static readonly string[] BrowserApiDocumentPrefixes =
    [
        "/api/site",
        "/api/browser"
    ];

    private readonly IReadOnlyDictionary<string, Uri> _sites;
    private readonly HashSet<string> _allowedOrigins;
    private readonly string _defaultSite;

    public BrowserUrlPolicy(BrowserOptions options)
    {
        _sites = options.GetSites();
        _defaultSite = _sites.ContainsKey(options.DefaultSite) ? options.DefaultSite : _sites.Keys.First();
        _allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in _sites.Values) _allowedOrigins.Add(Origin(site));
        foreach (var origin in options.GetAllowedExternalOrigins()) _allowedOrigins.Add(Origin(origin));
    }

    public IReadOnlyDictionary<string, Uri> Sites => _sites;
    public string DefaultSite => _defaultSite;

    public (string Site, Uri BaseUri) ResolveSite(string? site)
    {
        var key = string.IsNullOrWhiteSpace(site) ? _defaultSite : site.Trim().ToLowerInvariant();
        if (!_sites.TryGetValue(key, out var baseUri))
        {
            throw new BrowserApiException(StatusCodes.Status400BadRequest, "UNKNOWN_SITE", $"Неизвестный сайт '{key}'. Доступны: {string.Join(", ", _sites.Keys)}.");
        }

        return (key, baseUri);
    }

    public Uri BuildPageUri(string? site, string? path)
    {
        var (_, baseUri) = ResolveSite(site);
        return BuildPageUri(baseUri, path);
    }

    public Uri BuildPageUri(Uri baseUri, string? path)
    {
        var normalized = NormalizeRelativePath(path);
        var target = new Uri(baseUri, normalized);
        if (!SameOrigin(baseUri, target))
        {
            throw new BrowserApiException(StatusCodes.Status400BadRequest, "INVALID_PATH", "Путь должен вести только внутрь выбранного сайта TaskForge.");
        }

        return target;
    }

    public string NormalizeRelativePath(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        if (!value.StartsWith('/')) value = "/" + value;
        if (value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Contains('\0')
            || value.Any(char.IsControl)
            || Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw new BrowserApiException(StatusCodes.Status400BadRequest, "INVALID_PATH", "Разрешён только относительный путь TaskForge, например /courses.");
        }

        var queryIndex = value.IndexOfAny(['?', '#']);
        var pathOnly = queryIndex >= 0 ? value[..queryIndex] : value;
        var decodedPath = pathOnly;
        try
        {
            // Decode several times so double-encoded /api/browser, dot segments and
            // backslashes cannot bypass the document recursion/SSRF boundary.
            for (var depth = 0; depth < 4; depth++)
            {
                var next = Uri.UnescapeDataString(decodedPath);
                if (string.Equals(next, decodedPath, StringComparison.Ordinal)) break;
                decodedPath = next;
            }
        }
        catch (UriFormatException)
        {
            throw new BrowserApiException(StatusCodes.Status400BadRequest, "INVALID_PATH", "Путь содержит некорректное URL-кодирование.");
        }

        if (decodedPath.Contains('\\') || decodedPath.Any(char.IsControl))
        {
            throw new BrowserApiException(StatusCodes.Status400BadRequest, "INVALID_PATH", "Путь содержит запрещённые символы.");
        }

        var segments = decodedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(x => x is "." or ".."))
        {
            throw new BrowserApiException(StatusCodes.Status400BadRequest, "INVALID_PATH", "Переходы по каталогам в пути запрещены.");
        }

        if (BrowserApiDocumentPrefixes.Any(prefix => IsPathPrefix(decodedPath, prefix)))
        {
            throw new BrowserApiException(
                StatusCodes.Status400BadRequest,
                "BROWSER_API_RECURSION_BLOCKED",
                "Browser API не отображает собственные /api/site и /api/browser endpoints через Chromium.");
        }

        return value;
    }

    public bool IsAllowedRequest(Uri uri)
    {
        if (uri.Scheme is "data" or "blob" or "about") return true;
        if (uri.Scheme is not ("http" or "https" or "ws" or "wss")) return false;
        return _allowedOrigins.Contains(Origin(uri));
    }

    public bool IsSameTaskForgeOrigin(Uri uri)
        => _sites.Values.Any(site => SameOrigin(site, uri));

    public bool IsBrowserApiEndpoint(Uri uri)
    {
        if (!IsSameTaskForgeOrigin(uri)) return false;

        var path = uri.AbsolutePath;
        try
        {
            for (var depth = 0; depth < 4; depth++)
            {
                var next = Uri.UnescapeDataString(path);
                if (string.Equals(next, path, StringComparison.Ordinal)) break;
                path = next;
            }
        }
        catch (UriFormatException)
        {
            return true;
        }

        return BrowserApiDocumentPrefixes.Any(prefix => IsPathPrefix(path, prefix));
    }

    public static bool SameOrigin(Uri a, Uri b)
        => string.Equals(a.Scheme, NormalizeWebSocketScheme(b.Scheme), StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
           && EffectivePort(a) == EffectivePort(b);

    private static string Origin(Uri uri)
    {
        var scheme = NormalizeWebSocketScheme(uri.Scheme).ToLowerInvariant();
        var port = EffectivePort(uri);
        var defaultPort = scheme == Uri.UriSchemeHttps ? 443 : 80;
        return port == defaultPort ? $"{scheme}://{uri.Host.ToLowerInvariant()}" : $"{scheme}://{uri.Host.ToLowerInvariant()}:{port}";
    }

    private static string NormalizeWebSocketScheme(string scheme)
        => scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? Uri.UriSchemeHttps
            : scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ? Uri.UriSchemeHttp
            : scheme;

    private static int EffectivePort(Uri uri)
    {
        if (!uri.IsDefaultPort) return uri.Port;
        return NormalizeWebSocketScheme(uri.Scheme).Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
    }

    private static bool IsPathPrefix(string path, string prefix)
        => path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
}
