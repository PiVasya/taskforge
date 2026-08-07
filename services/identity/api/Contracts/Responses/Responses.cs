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

public sealed record ActivitySummaryDto(int SolvedAssignments, int TotalAttempts, int CodeSolutions, int ImageSolutions, int TestAttempts, int MathAttempts, int Score = 0)
{
    public static ActivitySummaryDto Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

public sealed record UserSummaryDto(
    Guid Id,
    Guid UserId,
    string Login,
    string Email,
    string MaskedEmail,
    string FirstName,
    string LastName,
    string? AvatarUrl,
    string? ProfilePictureUrl,
    string DisplayName,
    string FullName,
    string Role,
    string AccountType,
    string? Location,
    string? Education,
    bool ShowInLeaderboard,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);
