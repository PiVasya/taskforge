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
using static TaskForge.Identity.Api.Services.Mapping.IdentityApiMappingService;
using static TaskForge.Identity.Api.Services.Results.IdentityApiResultsService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

namespace TaskForge.Identity.Api.Services.Image;

internal static class IdentityApiImageService
{
    internal static object ToProfile(IdentityUser user, IReadOnlyCollection<string>? featureRoles = null) => new
    {
        user.Id,
        login = UserLoginOrFallback(user),
        user.Email,
        maskedEmail = MaskEmail(user.Email),
        user.FirstName,
        user.LastName,
        user.PhoneNumber,
        user.ProfilePictureUrl,
        user.AdditionalDataJson,
        displayName = DisplayName(user),
        user.Role,
        user.AccountType,
        isAi = string.Equals(user.AccountType, "ai", StringComparison.OrdinalIgnoreCase),
        roles = MergeRoles(user.Role, featureRoles),
        isAdmin = MergeRoles(user.Role, featureRoles).Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase)),
        isEditor = MergeRoles(user.Role, featureRoles).Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase) || string.Equals(r, "Editor", StringComparison.OrdinalIgnoreCase)),
        user.CreatedAt,
        user.LastLoginAt
    };

    internal static PublicProfileExtra ReadPublicProfileExtra(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return PublicProfileExtra.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var links = root.TryGetProperty("links", out var linkObj) && linkObj.ValueKind == JsonValueKind.Object ? linkObj : default(JsonElement);
            var skills = new List<string>();
            if (root.TryGetProperty("skills", out var skillsEl) && skillsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in skillsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) skills.Add(value.Trim());
                    }
                }
            }

            var showInLeaderboard = ReadJsonBool(root, "showInLeaderboard", true);

            return new PublicProfileExtra(
                PublicProfileEnabled: ReadJsonBool(root, "publicProfileEnabled", true),
                Bio: ReadJsonString(root, "bio"),
                Location: ReadJsonString(root, "location"),
                Education: ReadJsonString(root, "education"),
                Github: ReadJsonString(links, "github"),
                Telegram: ReadJsonString(links, "telegram"),
                Website: ReadJsonString(links, "website"),
                Skills: skills,
                ShowInLeaderboard: showInLeaderboard,
                ShowBio: ReadJsonBool(root, "showBio", false),
                ShowLocation: ReadJsonBool(root, "showLocation", false),
                ShowEducation: ReadJsonBool(root, "showEducation", false),
                ShowGithub: ReadJsonBool(root, "showGithub", false),
                ShowTelegram: ReadJsonBool(root, "showTelegram", false),
                ShowWebsite: ReadJsonBool(root, "showWebsite", false),
                ShowSkills: ReadJsonBool(root, "showSkills", false),
                ShowStats: ReadJsonBool(root, "showStats", false)
            );
        }
        catch
        {
            return PublicProfileExtra.Empty;
        }
    }

}
