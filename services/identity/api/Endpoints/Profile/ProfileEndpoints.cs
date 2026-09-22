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
using static TaskForge.Identity.Api.Services.Results.IdentityApiResultsService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

namespace TaskForge.Identity.Api.Endpoints;

internal static partial class IdentityApiEndpoints
{
    private static WebApplication MapProfileEndpoints(WebApplication app)
    {
        app.MapGet("/api/profile", async (HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            return user == null ? Unauthorized("Сессия истекла. Войдите заново.") : Microsoft.AspNetCore.Http.Results.Ok(ToProfile(user, await RolesForUser(db, user)));
        });

        app.MapGet("/api/profile/activity-summary", async (HttpContext http, IdentityDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");

            var result = await FetchUserActivitySummaryAsync(user.Id, cfg, httpFactory, ct, logger);
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                solvedAssignments = result.Summary.SolvedAssignments,
                totalAttempts = result.Summary.TotalAttempts,
                codeSolutions = result.Summary.CodeSolutions,
                imageSolutions = result.Summary.ImageSolutions,
                testAttempts = result.Summary.TestAttempts,
                mathAttempts = result.Summary.MathAttempts,
                score = result.Summary.Score,
                rating = result.Summary.Score,
                totalScore = result.Summary.Score,
                statsReliable = result.Reliable
            });
        });

        app.MapPut("/api/profile", async (ProfileUpdateRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");

            if (request.Login != null)
            {
                var login = NormalizeLogin(request.Login);
                if (!IsValidLogin(login, out var loginMessage)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = loginMessage });
                if (await db.Users.AnyAsync(x => x.Login == login && x.Id != user.Id)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Логин уже занят" });
                user.Login = login;
            }

            user.FirstName = (request.FirstName ?? user.FirstName).Trim();
            user.LastName = (request.LastName ?? user.LastName).Trim();
            if (request.PhoneNumber != null) user.PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
            if (request.ProfilePictureUrl != null) user.ProfilePictureUrl = string.IsNullOrWhiteSpace(request.ProfilePictureUrl) ? null : request.ProfilePictureUrl.Trim();
            if (request.AdditionalDataJson != null) user.AdditionalDataJson = string.IsNullOrWhiteSpace(request.AdditionalDataJson) ? null : request.AdditionalDataJson;
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToProfile(user, await RolesForUser(db, user)));
        });

        app.MapPost("/api/profile/change-password", async (ChangePasswordRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
        {
            if (await CheckAuthRateLimitAsync(http, "password") is { } limited) return limited;
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
            if (!VerifyPassword(request.CurrentPassword ?? string.Empty, user.PasswordSalt, user.PasswordHash)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Неверный текущий пароль" });
            if (!IsValidPassword(request.NewPassword, out var passwordMessage)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = passwordMessage });
            var salt = NewSalt();
            user.PasswordSalt = salt;
            user.PasswordHash = HashPassword(request.NewPassword ?? string.Empty, salt);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(new { message = "Пароль изменён" });
        });

        app.MapPost("/api/profile/change-email", async (ChangeEmailRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
        {
            if (await CheckAuthRateLimitAsync(http, "password") is { } limited) return limited;
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
            if (!VerifyPassword(request.Password ?? string.Empty, user.PasswordSalt, user.PasswordHash)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Неверный пароль" });
            var email = NormalizeOptionalEmail(request.NewEmail);
            if (!string.IsNullOrWhiteSpace(request.NewEmail) && string.IsNullOrWhiteSpace(email)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Email указан в неверном формате" });
            if (!string.IsNullOrWhiteSpace(email) && await db.Users.AnyAsync(x => x.Email == email && x.Id != user.Id)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Email уже занят" });
            user.Email = email;
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToProfile(user, await RolesForUser(db, user)));
        });

        app.MapPost("/api/profile/reveal-email", async (RevealEmailRequest request, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
        {
            if (await CheckAuthRateLimitAsync(http, "password") is { } limited) return limited;
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
            if (!VerifyPassword(request.Password ?? string.Empty, user.PasswordSalt, user.PasswordHash)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Неверный пароль" });
            return Microsoft.AspNetCore.Http.Results.Ok(new { email = user.Email, revealedAt = DateTimeOffset.UtcNow });
        });

        app.MapGet("/api/me/ui-settings", async (HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
            var row = await db.UiSettings.FindAsync(user.Id);
            if (row == null)
            {
                row = new UserUiSettings { UserId = user.Id, DataJson = DefaultUiSettingsJson() };
                db.UiSettings.Add(row);
                await db.SaveChangesAsync();
            }
            return Microsoft.AspNetCore.Http.Results.Text(row.DataJson, "application/json");
        });

        app.MapPut("/api/me/ui-settings", async (JsonElement payload, HttpContext http, IdentityDbContext db, IConfiguration cfg) =>
        {
            var user = await FindCurrentUserAsync(http, db, cfg);
            if (user == null) return Unauthorized("Сессия истекла. Войдите заново.");
            var row = await db.UiSettings.FindAsync(user.Id);
            if (row == null)
            {
                row = new UserUiSettings { UserId = user.Id };
                db.UiSettings.Add(row);
            }
            row.DataJson = payload.GetRawText();
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Text(row.DataJson, "application/json");
        });

        app.MapGet("/api/users/{userId:guid}/public-profile", async (Guid userId, IdentityDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Профиль не найден" });

            var extra = ReadPublicProfileExtra(user.AdditionalDataJson);
            if (!extra.PublicProfileEnabled) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Профиль не найден" });

            var statsResult = extra.ShowStats
                ? await FetchUserActivitySummaryAsync(userId, cfg, httpFactory, ct, app.Logger)
                : (ActivitySummaryDto.Empty, true);
            var stats = statsResult.Item1;
            var statsReliable = statsResult.Item2;
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                user.Id,
                login = UserLoginOrFallback(user),
                user.FirstName,
                user.LastName,
                avatarUrl = user.ProfilePictureUrl,
                profilePictureUrl = user.ProfilePictureUrl,
                displayName = PublicDisplayName(user),
                user.AccountType,
                isAi = string.Equals(user.AccountType, "ai", StringComparison.OrdinalIgnoreCase),
                user.CreatedAt,
                bio = extra.ShowBio ? extra.Bio : null,
                bioVisible = extra.ShowBio,
                location = extra.ShowLocation ? extra.Location : null,
                locationVisible = extra.ShowLocation,
                education = extra.ShowEducation ? extra.Education : null,
                educationVisible = extra.ShowEducation,
                github = extra.ShowGithub ? extra.Github : null,
                telegram = extra.ShowTelegram ? extra.Telegram : null,
                website = extra.ShowWebsite ? extra.Website : null,
                linksVisible = extra.ShowGithub || extra.ShowTelegram || extra.ShowWebsite,
                skills = extra.ShowSkills ? extra.Skills : Array.Empty<string>(),
                skillsVisible = extra.ShowSkills,
                showInLeaderboard = extra.ShowInLeaderboard,
                statsVisible = extra.ShowStats,
                statsReliable = !extra.ShowStats || statsReliable,
                solvedAssignments = extra.ShowStats && statsReliable ? stats.SolvedAssignments : (int?)null,
                totalAttempts = extra.ShowStats && statsReliable ? stats.TotalAttempts : (int?)null,
                codeSolutions = extra.ShowStats && statsReliable ? stats.CodeSolutions : (int?)null,
                imageSolutions = extra.ShowStats && statsReliable ? stats.ImageSolutions : (int?)null,
                testAttempts = extra.ShowStats && statsReliable ? stats.TestAttempts : (int?)null,
                mathAttempts = extra.ShowStats && statsReliable ? stats.MathAttempts : (int?)null,
                score = extra.ShowStats && statsReliable ? stats.Score : (int?)null,
                rating = extra.ShowStats && statsReliable ? stats.Score : (int?)null,
                totalScore = extra.ShowStats && statsReliable ? stats.Score : (int?)null
            });
        });

        return app;
    }
}
