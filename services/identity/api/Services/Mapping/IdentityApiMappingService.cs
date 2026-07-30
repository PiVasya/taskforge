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
using static TaskForge.Identity.Api.Services.Access.IdentityApiAccessService;
using static TaskForge.Identity.Api.Services.Common.IdentityApiCommonService;
using static TaskForge.Identity.Api.Services.Image.IdentityApiImageService;
using static TaskForge.Identity.Api.Services.Results.IdentityApiResultsService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

namespace TaskForge.Identity.Api.Services.Mapping;

internal static class IdentityApiMappingService
{
    internal static object ToAdminUserDto(IdentityUser user, IReadOnlyCollection<string>? featureRoles = null) => new
    {
        user.Id,
        login = UserLoginOrFallback(user),
        user.Email,
        maskedEmail = MaskEmail(user.Email),
        user.FirstName,
        user.LastName,
        user.PhoneNumber,
        user.ProfilePictureUrl,
        fullName = DisplayName(user),
        displayName = DisplayName(user),
        user.Role,
        roles = MergeRoles(user.Role, featureRoles),
        featureRoles = MergeRoles(user.Role, featureRoles),
        user.CreatedAt,
        user.LastLoginAt,
        user.AccountStatus,
        user.MergedIntoUserId,
        user.DeletedAtUtc,
        telegramUsername = user.TelegramUsername,
        telegramLinkedAtUtc = user.TelegramLinkedAtUtc,
        minecraftNick = (string?)null,
        minecraftLinkedAtUtc = (DateTimeOffset?)null,
        emailConfirmed = true,
        lockoutEnabled = false,
        codeSolutions = 0,
        passedTests = 0,
        imageSolutions = 0,
        mathSolutions = 0,
        integrationDataReliable = false,
        solutionStatsReliable = false
    };

    internal static UserSummaryDto ToUserSummaryDto(IdentityUser user)
    {
        var extra = ReadPublicProfileExtra(user.AdditionalDataJson);
        return new UserSummaryDto(
            user.Id,
            user.Id,
            UserLoginOrFallback(user),
            user.Email ?? string.Empty,
            MaskEmail(user.Email),
            user.FirstName,
            user.LastName,
            user.ProfilePictureUrl,
            user.ProfilePictureUrl,
            DisplayName(user),
            DisplayName(user),
            user.Role,
            extra.ShowLocation ? extra.Location : null,
            extra.ShowEducation ? extra.Education : null,
            extra.ShowInLeaderboard,
            user.CreatedAt,
            user.LastLoginAt);
    }

    internal static string BuildBackfilledLogin(IdentityUser user, HashSet<string> used)
    {
        var basePart = NormalizeLogin(user.Email);
        if (!IsValidLogin(basePart, out _)) basePart = "user";

        var suffix = user.Id.ToString("N")[..8].ToLowerInvariant();
        var maxBaseLength = System.Math.Max(3, 64 - suffix.Length - 1);
        if (basePart.Length > maxBaseLength) basePart = basePart[..maxBaseLength].Trim('.', '-', '_');
        if (!IsValidLogin(basePart, out _)) basePart = "user";

        var candidate = $"{basePart}-{suffix}";
        var counter = 2;
        while (used.Contains(candidate))
        {
            var counterSuffix = $"{suffix}-{counter}";
            maxBaseLength = System.Math.Max(3, 64 - counterSuffix.Length - 1);
            var trimmedBase = basePart.Length > maxBaseLength ? basePart[..maxBaseLength].Trim('.', '-', '_') : basePart;
            if (!IsValidLogin(trimmedBase, out _)) trimmedBase = "user";
            candidate = $"{trimmedBase}-{counterSuffix}";
            counter++;
        }

        return candidate;
    }

    internal static void ValidateProductionIdentityConfig(IConfiguration cfg, IHostEnvironment env)
    {
        if (!env.IsProduction()) return;

        var jwt = cfg["Jwt:Key"] ?? cfg["Jwt:SigningKey"];
        if (IsUnsafeProductionSecret(jwt, minLength: 48))
        {
            throw new InvalidOperationException("Production JWT signing key is missing, weak or still uses a placeholder.");
        }

        var bootstrapAdminEmails = (cfg["Bootstrap:AdminEmails"] ?? string.Empty).Trim();
        var firstUserIsAdmin = cfg.GetValue("Bootstrap:FirstUserIsAdmin", false);
        if (firstUserIsAdmin && string.IsNullOrWhiteSpace(bootstrapAdminEmails))
        {
            throw new InvalidOperationException("Production must not rely on first-user-is-admin without explicit Bootstrap:AdminEmails.");
        }
    }

    internal static string MaskEmail(string? email)
    {
        var value = (email ?? string.Empty).Trim();
        var at = value.IndexOf('@');
        if (at <= 0) return "Почта не указана";
        var name = value[..at];
        var domain = value[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        var host = dot > 0 ? domain[..dot] : domain;
        var zone = dot > 0 ? domain[(dot + 1)..] : string.Empty;
        var maskedName = name.Length <= 2 ? $"{name[..1]}***" : $"{name[..System.Math.Min(2, name.Length)]}***";
        var maskedHost = host.Length <= 2 ? $"{host[..1]}***" : $"{host[..System.Math.Min(2, host.Length)]}***";
        return string.IsNullOrWhiteSpace(zone) ? $"{maskedName}@{maskedHost}" : $"{maskedName}@{maskedHost}.{zone}";
    }

}
