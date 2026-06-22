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

public sealed record RegisterRequest(string? Login, string? Email, string? Password, string? FirstName, string? LastName, string? PhoneNumber, string? AdditionalDataJson);

public sealed record LoginRequest(string? Login, string? Email, string? Password);

public sealed record ProfileUpdateRequest(string? Login, string? FirstName, string? LastName, string? PhoneNumber, string? ProfilePictureUrl, string? AdditionalDataJson);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record ChangeEmailRequest(string? NewEmail, string? Password);

public sealed record RevealEmailRequest(string? Password);

public sealed record AdminUserUpdateRequest(string? Login, string? Email, string? FirstName, string? LastName, string? PhoneNumber, string? ProfilePictureUrl, string? Role);

public sealed record RoleAssignRequest(string? Code);

public sealed record FeatureRoleRequest(string? Code, string? Title, string? Description, bool? IsActive);

public sealed record UserIdsRequest(Guid[]? UserIds);

public sealed record TelegramConfirmRequest(string? Code, long ChatId, string? Username);
