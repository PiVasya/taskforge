using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

using TaskForge.Solutions.Api.Contracts;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Image.SolutionsApiImageService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private static WebApplication MapBadgesEndpoints(WebApplication app)
    {
        app.MapGet("/api/badges", async (SolutionsDbContext db) =>
        {
            var rows = await db.Badges.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => BadgeDto(x)).ToList());
        });

        app.MapPost("/api/badges", async (HttpRequest request, SolutionsDbContext db, CancellationToken ct) =>
        {
            IFormCollection form;
            try { form = await request.ReadFormAsync(ct); }
            catch (Exception ex) { return Problem(400, "BADGE_FORM_READ_FAILED", "badges.form", "Не удалось прочитать форму создания бейджа.", ex.Message); }
            var name = form.TryGetValue("name", out var n) ? n.ToString().Trim() : string.Empty;
            var description = form.TryGetValue("description", out var d) ? d.ToString().Trim() : null;
            var file = form.Files.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(name)) return Problem(400, "BADGE_NAME_REQUIRED", "badges.validation", "Введите название бейджа.");
            if (file == null || file.Length == 0) return Problem(400, "BADGE_FILE_REQUIRED", "badges.validation", "Выберите SVG-файл бейджа.");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".svg" && file.ContentType != "image/svg+xml") return Problem(400, "BADGE_SVG_REQUIRED", "badges.validation", "Разрешены только SVG-файлы бейджей.");
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(ms.ToArray());
            var badge = new Badge { Name = name, Description = string.IsNullOrWhiteSpace(description) ? null : description, ImageUrl = dataUri };
            db.Badges.Add(badge);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(BadgeDto(badge));
        }).DisableAntiforgery();

        app.MapDelete("/api/badges/{badgeId:guid}", async (Guid badgeId, SolutionsDbContext db) =>
        {
            var badge = await db.Badges.FindAsync(badgeId);
            if (badge == null) return Microsoft.AspNetCore.Http.Results.NoContent();
            db.UserBadges.RemoveRange(await db.UserBadges.Where(x => x.BadgeId == badgeId).ToListAsync());
            db.Badges.Remove(badge);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.NoContent();
        });

        app.MapPost("/api/badges/award", async (BadgeUserRequest req, SolutionsDbContext db) =>
        {
            if (!await db.Badges.AnyAsync(x => x.Id == req.BadgeId)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Бейдж не найден.", code = "BADGE_NOT_FOUND" });
            if (!await db.UserBadges.AnyAsync(x => x.UserId == req.UserId && x.BadgeId == req.BadgeId)) db.UserBadges.Add(new UserBadge { UserId = req.UserId, BadgeId = req.BadgeId });
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(new { awarded = true });
        });

        app.MapPost("/api/badges/revoke", async (BadgeUserRequest req, SolutionsDbContext db) =>
        {
            var rows = await db.UserBadges.Where(x => x.UserId == req.UserId && x.BadgeId == req.BadgeId).ToListAsync();
            db.UserBadges.RemoveRange(rows);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(new { revoked = true });
        });

        app.MapGet("/api/badges/user/{userId:guid}", async (Guid userId, SolutionsDbContext db) =>
        {
            var badgeIds = await db.UserBadges.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.AwardedAt).Select(x => x.BadgeId).ToListAsync();
            var rows = await db.Badges.AsNoTracking().Where(x => badgeIds.Contains(x.Id)).ToListAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => BadgeDto(x)).ToList());
        });

        return app;
    }
}
