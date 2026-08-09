using TaskForge.Browser.Api.Configuration;

namespace TaskForge.Browser.Api.Security;

public sealed class BrowserUrlPolicy
{
    private static readonly string[] BrowserApiDocumentPrefixes =
    [
        "/api/site",
        "/api/browser",
        "/api/ai/browser"
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

        if (value.Contains('\\') || value.Contains('\0') || value.Any(char.IsControl))
        {
            throw InvalidPath("Путь содержит запрещённые символы.");
        }

        // Validate only the document path. Query values are allowed to contain URLs
        // (for example ?returnUrl=https://...), because they do not control the
        // top-level Chromium origin.
        var rawPathOnly = PathPart(value);
        if (rawPathOnly.StartsWith("//", StringComparison.Ordinal) || HasUriScheme(rawPathOnly))
        {
            throw InvalidPath("Разрешён только относительный путь TaskForge, например /courses.");
        }

        if (!value.StartsWith('/')) value = "/" + value;

        var normalizedPathOnly = PathPart(value);
        var decodedPath = DecodePath(normalizedPathOnly);

        // Re-check the dangerous forms after repeated URL decoding so values such
        // as /%2f%2fevil.example and /%252e%252e/ cannot bypass the boundary.
        if (decodedPath.Contains('\\')
            || decodedPath.Any(char.IsControl)
            || decodedPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw InvalidPath("Путь содержит запрещённые или абсолютные URL-компоненты.");
        }

        var segments = decodedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(x => x is "." or ".."))
        {
            throw InvalidPath("Переходы по каталогам в пути запрещены.");
        }

        if (BrowserApiDocumentPrefixes.Any(prefix => IsPathPrefix(decodedPath, prefix)))
        {
            throw new BrowserApiException(
                StatusCodes.Status400BadRequest,
                "BROWSER_API_RECURSION_BLOCKED",
                "Browser API не отображает собственные /api/site, /api/browser и /api/ai/browser endpoints через Chromium.");
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

        string path;
        try
        {
            path = DecodePath(uri.AbsolutePath);
        }
        catch (BrowserApiException)
        {
            // An undecodable Browser API-looking request is safer to block than to
            // let Chromium recurse into the browser service.
            return true;
        }

        return BrowserApiDocumentPrefixes.Any(prefix => IsPathPrefix(path, prefix));
    }

    public static bool SameOrigin(Uri a, Uri b)
        => string.Equals(a.Scheme, NormalizeWebSocketScheme(b.Scheme), StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
           && EffectivePort(a) == EffectivePort(b);

    private static string PathPart(string value)
    {
        var index = value.IndexOfAny(['?', '#']);
        return index >= 0 ? value[..index] : value;
    }

    private static string DecodePath(string path)
    {
        var decoded = path;
        try
        {
            const int maxDecodeDepth = 8;
            for (var depth = 0; depth < maxDecodeDepth; depth++)
            {
                var next = Uri.UnescapeDataString(decoded);
                if (string.Equals(next, decoded, StringComparison.Ordinal)) return decoded;
                decoded = next;
            }

            // Do not accept intentionally over-nested encoding. It serves no normal
            // routing purpose and makes security checks depend on how many times a
            // downstream component decides to decode the path.
            var afterLimit = Uri.UnescapeDataString(decoded);
            if (!string.Equals(afterLimit, decoded, StringComparison.Ordinal))
            {
                throw InvalidPath("Путь имеет слишком глубокое URL-кодирование.");
            }
        }
        catch (UriFormatException)
        {
            throw InvalidPath("Путь содержит некорректное URL-кодирование.");
        }

        return decoded;
    }

    private static bool HasUriScheme(string value)
    {
        if (string.IsNullOrEmpty(value) || !IsAsciiLetter(value[0])) return false;

        for (var index = 1; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == ':') return true;
            if (ch == '/' || ch == '?' || ch == '#') return false;
            if (!(IsAsciiLetter(ch) || IsAsciiDigit(ch) || ch is '+' or '-' or '.')) return false;
        }

        return false;
    }

    private static bool IsAsciiLetter(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char value)
        => value is >= '0' and <= '9';

    private static BrowserApiException InvalidPath(string message)
        => new(StatusCodes.Status400BadRequest, "INVALID_PATH", message);

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
