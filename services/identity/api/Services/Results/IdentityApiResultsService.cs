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
using static TaskForge.Identity.Api.Services.Mapping.IdentityApiMappingService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

namespace TaskForge.Identity.Api.Services.Results;

internal static class IdentityApiResultsService
{
    internal static int UserSearchScore(IdentityUser user, string query)
    {
        var values = new[]
        {
            user.Login ?? string.Empty,
            user.Email ?? string.Empty,
            user.FirstName,
            user.LastName,
            DisplayName(user),
            user.Id.ToString()
        }
        .Select(NormalizeSearch)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .ToArray();

        if (values.Any(x => x.Contains(query, StringComparison.OrdinalIgnoreCase))) return 0;

        var best = int.MaxValue;
        foreach (var value in values)
        {
            best = System.Math.Min(best, Levenshtein(value, query));
            foreach (var token in value.Split(new[] { ' ', '@', '.', '_', '-', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                best = System.Math.Min(best, Levenshtein(token, query));
            }
        }
        return best == int.MaxValue ? 999 : best;
    }

}
