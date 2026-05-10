using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuizTaskService.Data;
using QuizTaskService.Data.Entities;
using QuizTaskService.DTO;
using QuizTaskService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddDbContext<QuizDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("QuizConnection"));
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = ClaimTypes.NameIdentifier,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrWhiteSpace(context.Token) && context.Request.Cookies.TryGetValue("tf_at", out var token))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuizDbContext>();
    var migrateOnStartup = builder.Configuration.GetValue("Database:MigrateOnStartup", true);
    var ensureCreated = builder.Configuration.GetValue("Database:EnsureCreated", false);

    if (migrateOnStartup)
    {
        app.Logger.LogInformation("Applying QuizDbContext migrations...");
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("QuizDbContext migrations applied.");
    }
    else if (ensureCreated)
    {
        await db.Database.EnsureCreatedAsync();
    }

    if (builder.Configuration.GetValue("Seed:A1Samples", false))
    {
        await QuizSeedService.SeedA1SamplesAsync(db);
    }
}

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "quiz-task-service" }));
app.MapGet("/health/ready", async (QuizDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
});

app.MapGet("/api/quiz/tasks", async (QuizDbContext db, string? subjectCode, string? examCode, string? sectionCode, string? type, bool includeDraft = false) =>
{
    var query = db.Tasks.AsNoTracking().AsQueryable();
    if (!includeDraft) query = query.Where(x => x.IsPublished);
    if (!string.IsNullOrWhiteSpace(subjectCode)) query = query.Where(x => x.SubjectCode == subjectCode);
    if (!string.IsNullOrWhiteSpace(examCode)) query = query.Where(x => x.ExamCode == examCode);
    if (!string.IsNullOrWhiteSpace(sectionCode)) query = query.Where(x => x.SectionCode == sectionCode);
    if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.Type == type);

    var taskEntities = await query
        .OrderBy(x => x.SectionCode)
        .ThenBy(x => x.Difficulty)
        .ThenBy(x => x.Title)
        .ToListAsync();

    return Results.Ok(taskEntities.Select(QuizTaskDto.FromEntity).ToList());
});

app.MapGet("/api/quiz/tasks/{idOrSlug}", async (QuizDbContext db, ClaimsPrincipal user, string idOrSlug) =>
{
    var isGuid = Guid.TryParse(idOrSlug, out var id);
    var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => isGuid ? x.Id == id : x.Slug == idOrSlug);
    if (task == null || !task.IsPublished) return Results.NotFound();

    var version = await db.TaskVersions.AsNoTracking()
        .Where(x => x.TaskId == task.Id && x.VersionNumber == task.CurrentVersion)
        .FirstOrDefaultAsync();

    if (version == null) return Results.NotFound(new { message = "Task version not found" });

    var userId = TryGetUserId(user);
    var progress = userId.HasValue
        ? await db.Progress.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId.Value && x.TaskId == task.Id)
        : null;

    return Results.Ok(new QuizTaskDetailsDto(
        QuizTaskDto.FromEntity(task),
        version.Id,
        version.VersionNumber,
        version.DataJson,
        version.ExplanationJson,
        progress?.Solved ?? false,
        progress?.BestScorePercent ?? 0));
});

app.MapPost("/api/quiz/tasks/{taskId:guid}/attempts", [Authorize] async (QuizDbContext db, ClaimsPrincipal user, Guid taskId, [FromBody] SubmitQuizAttemptRequest req) =>
{
    var userId = TryGetUserId(user);
    if (!userId.HasValue) return Results.Unauthorized();

    var clientAttemptId = req.ClientAttemptId ?? Guid.NewGuid();
    var existing = await db.Attempts.FirstOrDefaultAsync(x => x.UserId == userId.Value && x.ClientAttemptId == clientAttemptId);
    if (existing != null)
    {
        var existingProgress = await db.Progress.AsNoTracking().FirstAsync(x => x.UserId == userId.Value && x.TaskId == existing.TaskId);
        var existingVersion = await db.TaskVersions.AsNoTracking().FirstAsync(x => x.Id == existing.TaskVersionId);
        return Results.Ok(ToResult(existing, existingVersion.ExplanationJson, existingProgress));
    }

    var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == taskId && x.IsPublished);
    if (task == null) return Results.NotFound();

    var version = await db.TaskVersions
        .Where(x => x.TaskId == task.Id && x.VersionNumber == task.CurrentVersion)
        .FirstOrDefaultAsync();
    if (version == null) return Results.NotFound(new { message = "Task version not found" });

    var isCorrect = QuizAnswerChecker.IsCorrect(req.Answer, version.CorrectAnswerJson);
    var score = isCorrect ? 1m : 0m;
    var now = DateTime.UtcNow;

    var attempt = new QuizAttempt
    {
        TaskId = task.Id,
        TaskVersionId = version.Id,
        UserId = userId.Value,
        ClientAttemptId = clientAttemptId,
        AnswerJson = req.Answer.GetRawText(),
        IsCorrect = isCorrect,
        Score = score,
        MaxScore = 1m,
        TimeSpentSeconds = req.TimeSpentSeconds,
        CreatedAt = now
    };

    db.Attempts.Add(attempt);

    var progress = await db.Progress.FirstOrDefaultAsync(x => x.UserId == userId.Value && x.TaskId == task.Id);
    if (progress == null)
    {
        progress = new QuizProgress
        {
            UserId = userId.Value,
            TaskId = task.Id,
            Solved = isCorrect,
            BestScore = score,
            BestScorePercent = score * 100m,
            AttemptsCount = 1,
            LastAttemptId = attempt.Id,
            FirstSolvedAt = isCorrect ? now : null,
            LastAttemptAt = now
        };
        db.Progress.Add(progress);
    }
    else
    {
        progress.AttemptsCount += 1;
        progress.LastAttemptAt = now;
        progress.LastAttemptId = attempt.Id;
        if (score > progress.BestScore)
        {
            progress.BestScore = score;
            progress.BestScorePercent = score * 100m;
        }
        if (isCorrect && !progress.Solved)
        {
            progress.Solved = true;
            progress.FirstSolvedAt = now;
        }
    }

    await db.SaveChangesAsync();
    return Results.Ok(ToResult(attempt, version.ExplanationJson, progress));
});

app.MapGet("/api/quiz/me/progress", [Authorize] async (QuizDbContext db, ClaimsPrincipal user, string? sectionCode) =>
{
    var userId = TryGetUserId(user);
    if (!userId.HasValue) return Results.Unauthorized();

    var query = db.Progress.AsNoTracking().Where(x => x.UserId == userId.Value);
    if (!string.IsNullOrWhiteSpace(sectionCode))
    {
        var taskIds = await db.Tasks.AsNoTracking()
            .Where(x => x.SectionCode == sectionCode)
            .Select(x => x.Id)
            .ToListAsync();
        query = query.Where(x => taskIds.Contains(x.TaskId));
    }

    var progressEntities = await query
        .OrderByDescending(x => x.LastAttemptAt)
        .ToListAsync();

    return Results.Ok(progressEntities.Select(QuizProgressDto.FromEntity).ToList());
});

app.MapPost("/api/admin/quiz/tasks", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, [FromBody] CreateQuizTaskRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Slug) || string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Prompt))
    {
        return Results.BadRequest(new { message = "Slug, Title and Prompt are required" });
    }

    var exists = await db.Tasks.AnyAsync(x => x.Slug == req.Slug.Trim());
    if (exists) return Results.Conflict(new { message = "Task slug already exists" });

    static string JsonOrDefault(JsonElement? element, string? json, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(json)) return json;
        return element.HasValue ? element.Value.GetRawText() : fallback;
    }

    var task = new QuizTask
    {
        Slug = req.Slug.Trim(),
        Type = string.IsNullOrWhiteSpace(req.Type) ? "single-choice" : req.Type.Trim(),
        Title = req.Title.Trim(),
        Prompt = req.Prompt.Trim(),
        SubjectCode = string.IsNullOrWhiteSpace(req.SubjectCode) ? "russian" : req.SubjectCode.Trim(),
        ExamCode = string.IsNullOrWhiteSpace(req.ExamCode) ? "ct-ce-2026" : req.ExamCode.Trim(),
        SectionCode = req.SectionCode?.Trim(),
        Difficulty = req.Difficulty <= 0 ? 1 : req.Difficulty,
        TagsJson = JsonOrDefault(req.Tags, req.TagsJson, "[]"),
        SourceName = req.SourceName,
        SourceYear = req.SourceYear,
        IsPublished = req.IsPublished,
        CurrentVersion = 1
    };

    var version = new QuizTaskVersion
    {
        TaskId = task.Id,
        VersionNumber = 1,
        DataJson = JsonOrDefault(req.Data, req.DataJson, "{}"),
        CorrectAnswerJson = JsonOrDefault(req.CorrectAnswer, req.CorrectAnswerJson, "{}"),
        ExplanationJson = JsonOrDefault(req.Explanation, req.ExplanationJson, "{}"),
        ChangeComment = "Initial version"
    };

    db.Tasks.Add(task);
    db.TaskVersions.Add(version);
    await db.SaveChangesAsync();

    return Results.Ok(new QuizTaskDetailsDto(QuizTaskDto.FromEntity(task), version.Id, version.VersionNumber, version.DataJson, version.ExplanationJson, false, 0));
});

app.Run();

static Guid? TryGetUserId(ClaimsPrincipal user)
{
    var raw = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? user.FindFirstValue("nameid");
    return Guid.TryParse(raw, out var id) ? id : null;
}

static QuizAttemptResultDto ToResult(QuizAttempt attempt, string explanationJson, QuizProgress progress)
{
    var percent = attempt.MaxScore <= 0 ? 0 : Math.Round(attempt.Score / attempt.MaxScore * 100m, 2);
    return new QuizAttemptResultDto(
        attempt.Id,
        attempt.TaskId,
        attempt.TaskVersionId,
        attempt.IsCorrect,
        attempt.Score,
        attempt.MaxScore,
        percent,
        explanationJson,
        QuizProgressDto.FromEntity(progress));
}
