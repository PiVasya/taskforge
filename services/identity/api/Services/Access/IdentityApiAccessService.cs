using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;

using TaskForge.Identity.Api.Contracts;
using static TaskForge.Identity.Api.Services.Common.IdentityApiCommonService;
using static TaskForge.Identity.Api.Services.Image.IdentityApiImageService;
using static TaskForge.Identity.Api.Services.Mapping.IdentityApiMappingService;
using static TaskForge.Identity.Api.Services.Results.IdentityApiResultsService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

namespace TaskForge.Identity.Api.Services.Access;

internal static class IdentityApiAccessService
{
    internal static async Task SeedFeatureRoles(IdentityDbContext db)
    {
        var defaults = new[]
        {
            new FeatureRole { Code = "Admin", Title = "Администратор", Description = "Полный доступ к админке", IsActive = true },
            new FeatureRole { Code = "Editor", Title = "Редактор", Description = "Редактирование заданий и курсов", IsActive = true },
            new FeatureRole { Code = "LearningEditor", Title = "Редактор ЦТ", Description = "Редактирование learning/quiz контента", IsActive = true },
            new FeatureRole { Code = "Minecraft", Title = "Minecraft", Description = "Доступ к Minecraft-инструментам", IsActive = true }
        };
        foreach (var role in defaults)
        {
            if (!await db.FeatureRoles.AnyAsync(x => x.Code == role.Code)) db.FeatureRoles.Add(role);
        }
        await db.SaveChangesAsync();
    }

    internal static async Task<string[]> RolesForUser(IdentityDbContext db, IdentityUser user)
    {
        var rows = await db.UserFeatureRoles.AsNoTracking().Where(x => x.UserId == user.Id).Select(x => x.Code).ToListAsync();
        return MergeRoles(user.Role, rows);
    }

    internal static string[] MergeRoles(string? primaryRole, IEnumerable<string>? featureRoles)
    {
        return new[] { NormalizeRole(primaryRole) }
            .Concat(featureRoles ?? Array.Empty<string>())
            .Select(NormalizeRoleCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string ResolveInitialRole(string email, bool firstUser, IConfiguration cfg)
    {
        var adminEmails = (cfg["Bootstrap:AdminEmails"] ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeEmail)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (adminEmails.Contains(email)) return "Admin";

        var firstUserIsAdmin = cfg.GetValue("Bootstrap:FirstUserIsAdmin", false);
        return firstUser && firstUserIsAdmin ? "Admin" : "User";
    }

    internal static bool IsValidPassword(string? password, out string message)
    {
        var value = password ?? string.Empty;
        if (value.Length < 8)
        {
            message = "Пароль должен быть не короче 8 символов.";
            return false;
        }

        if (value.Length > 256)
        {
            message = "Пароль слишком длинный.";
            return false;
        }

        if (value.All(char.IsWhiteSpace))
        {
            message = "Пароль не может состоять только из пробелов.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    internal static string HashPassword(string password, string salt)
    {
        const int iterations = 210_000;
        var saltBytes = Convert.FromBase64String(salt);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(password ?? string.Empty),
            salt: saltBytes,
            iterations: iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);
        return $"PBKDF2-SHA256${iterations}${Convert.ToBase64String(hash)}";
    }

    internal static bool VerifyPassword(string password, string salt, string hash)
    {
        try
        {
            if (hash.StartsWith("PBKDF2-SHA256$", StringComparison.Ordinal))
            {
                var parts = hash.Split('$');
                if (parts.Length != 3 || !int.TryParse(parts[1], out var iterations) || iterations < 100_000) return false;
                var saltBytes = Convert.FromBase64String(salt);
                var expected = Convert.FromBase64String(parts[2]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(
                    password: Encoding.UTF8.GetBytes(password ?? string.Empty),
                    salt: saltBytes,
                    iterations: iterations,
                    hashAlgorithm: HashAlgorithmName.SHA256,
                    outputLength: expected.Length);
                return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
            }

            // Legacy v17 and older used SHA256(salt + ":" + password). Keep read support and migrate on successful login.
            using var sha = SHA256.Create();
            var legacy = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(salt + ":" + (password ?? string.Empty))));
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(legacy), Convert.FromBase64String(hash));
        }
        catch
        {
            return false;
        }
    }

    internal static bool NeedsPasswordRehash(string? hash) => string.IsNullOrWhiteSpace(hash) || !hash.StartsWith("PBKDF2-SHA256$", StringComparison.Ordinal);

    internal static ClaimsPrincipal? ValidateToken(string? token, IConfiguration cfg, bool validateLifetime)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var key = Encoding.UTF8.GetBytes(cfg["Jwt:Key"] ?? cfg["Jwt:SigningKey"] ?? "dev_change_me_please_change_me_please_32_chars");
            return new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = validateLifetime,
                ValidIssuer = cfg["Jwt:Issuer"] ?? "TaskForge",
                ValidAudience = cfg["Jwt:Audience"] ?? "TaskForge",
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ClockSkew = TimeSpan.FromSeconds(30)
            }, out _);
        }
        catch { return null; }
    }

    internal static Guid? TryGetUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    internal static async Task<IdentityUser?> FindCurrentUserAsync(HttpContext http, IdentityDbContext db, IConfiguration cfg)
    {
        var principal = ValidateToken(ReadBearer(http) ?? ReadCookie(http, "tf_at"), cfg, validateLifetime: true);
        var uid = principal == null ? null : TryGetUserId(principal);
        return uid == null ? null : await db.Users.FindAsync(uid.Value);
    }

    internal static string CreateJwt(IdentityUser user, IConfiguration cfg, TimeSpan lifetime, string tokenType, IReadOnlyCollection<string>? featureRoles = null)
    {
        var key = Encoding.UTF8.GetBytes(cfg["Jwt:Key"] ?? cfg["Jwt:SigningKey"] ?? "dev_change_me_please_change_me_please_32_chars");
        var roles = MergeRoles(user.Role, featureRoles);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, UserLoginOrFallback(user)),
            new("login", UserLoginOrFallback(user)),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new("role", user.Role),
            new("roles", string.Join(',', roles)),
            new("primary_role", user.Role),
            new("account_type", NormalizeAccountType(user.AccountType)),
            new("token_type", tokenType),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        var creds = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(cfg["Jwt:Issuer"] ?? "TaskForge", cfg["Jwt:Audience"] ?? "TaskForge", claims, expires: DateTime.UtcNow.Add(lifetime), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

}
