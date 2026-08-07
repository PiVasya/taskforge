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


namespace TaskForge.Identity.Api.Contracts;

public sealed record PublicProfileExtra(
    bool PublicProfileEnabled,
    string? Bio,
    string? Location,
    string? Education,
    string? Github,
    string? Telegram,
    string? Website,
    IReadOnlyList<string> Skills,
    bool ShowInLeaderboard,
    bool ShowBio,
    bool ShowLocation,
    bool ShowEducation,
    bool ShowGithub,
    bool ShowTelegram,
    bool ShowWebsite,
    bool ShowSkills,
    bool ShowStats)
{
    public static PublicProfileExtra Empty { get; } = new(true, null, null, null, null, null, null, Array.Empty<string>(), true, false, false, false, false, false, false, false, false);
}
