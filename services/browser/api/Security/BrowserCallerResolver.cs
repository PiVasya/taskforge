using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TaskForge.Browser.Api.Contracts;

namespace TaskForge.Browser.Api.Security;

public sealed class BrowserCallerResolver(IConfiguration configuration, ILogger<BrowserCallerResolver> logger)
{
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<BrowserCallerResolver> _logger = logger;
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    public BrowserCaller Resolve(HttpContext http)
    {
        var network = ResolveNetworkAddress(http);
        var networkKey = Hash($"network:{network}");
        var token = ReadAccessToken(http);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new BrowserCaller(false, null, "anonymous", null, $"anonymous:{networkKey}", networkKey, false);
        }

        try
        {
            var principal = _handler.ValidateToken(token, BuildValidationParameters(), out var validated);
            if (validated is not JwtSecurityToken jwt
                || !string.Equals(jwt.Header.Alg, SecurityAlgorithms.HmacSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityTokenValidationException("Unexpected JWT algorithm.");
            }

            var tokenType = principal.FindFirstValue("token_type");
            if (!string.IsNullOrWhiteSpace(tokenType)
                && !string.Equals(tokenType, "access", StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityTokenValidationException("Only access tokens are accepted.");
            }

            var rawUserId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue("userId");
            if (!Guid.TryParse(rawUserId, out var userId))
            {
                throw new SecurityTokenValidationException("The access token does not contain a valid user id.");
            }

            var accountType = NormalizeAccountType(principal.FindFirstValue("account_type"));
            return new BrowserCaller(true, userId, accountType, token, $"user:{userId:N}", networkKey, true);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            _logger.LogInformation("Browser API rejected an invalid caller credential: {ErrorType}", ex.GetType().Name);
            return new BrowserCaller(false, null, "anonymous", null, $"anonymous:{networkKey}", networkKey, true);
        }
    }

    public void ThrowIfInvalidCredential(BrowserCaller caller)
    {
        if (caller.CredentialWasPresented && !caller.IsAuthenticated)
        {
            throw new BrowserApiException(StatusCodes.Status401Unauthorized, "INVALID_ACCESS_TOKEN", "Переданный access token недействителен или истёк.");
        }
    }

    private TokenValidationParameters BuildValidationParameters()
    {
        var key = _configuration["Jwt:Key"] ?? _configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Jwt:Key is not configured for browser-api.");
        }

        return new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateIssuer = true,
            ValidIssuer = _configuration["Jwt:Issuer"] ?? "TaskForge",
            ValidateAudience = true,
            ValidAudience = _configuration["Jwt:Audience"] ?? "TaskForge",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(20),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = ClaimTypes.Role
        };
    }

    private static string? ReadAccessToken(HttpContext http)
    {
        var authorization = http.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization[7..].Trim();
        }

        // Browser API authentication is explicit on purpose. Ambient TaskForge cookies
        // are ignored so a third-party page cannot turn a human browser session into
        // an authenticated headless-browser session through CSRF/CORS side effects.
        return null;
    }

    private static string ResolveNetworkAddress(HttpContext http)
    {
        foreach (var value in new[]
                 {
                     http.Request.Headers["X-TaskForge-Client-IP"].FirstOrDefault(),
                     http.Request.Headers["X-Real-IP"].FirstOrDefault(),
                     http.Connection.RemoteIpAddress?.ToString()
                 })
        {
            if (IPAddress.TryParse(value, out var address))
            {
                return address.MapToIPv6().ToString();
            }
        }

        return "unknown";
    }


    private static string NormalizeAccountType(string? value)
        => string.Equals(value, "ai", StringComparison.OrdinalIgnoreCase) ? "ai" : "human";

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
}
