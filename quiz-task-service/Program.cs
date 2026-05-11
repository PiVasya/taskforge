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
    var existingByClientAttempt = await db.Attempts.AsNoTracking()
        .FirstOrDefaultAsync(x => x.UserId == userId.Value && x.ClientAttemptId == clientAttemptId);
    if (existingByClientAttempt != null)
    {
        var existingProgress = await db.Progress.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId.Value && x.TaskId == existingByClientAttempt.TaskId);
        var existingVersion = await db.TaskVersions.AsNoTracking().FirstAsync(x => x.Id == existingByClientAttempt.TaskVersionId);

        existingProgress ??= new QuizProgress
        {
            UserId = userId.Value,
            TaskId = existingByClientAttempt.TaskId,
            Solved = existingByClientAttempt.IsCorrect,
            BestScore = existingByClientAttempt.Score,
            BestScorePercent = existingByClientAttempt.MaxScore <= 0 ? 0 : existingByClientAttempt.Score / existingByClientAttempt.MaxScore * 100m,
            AttemptsCount = 1,
            LastAttemptId = existingByClientAttempt.Id,
            FirstSolvedAt = existingByClientAttempt.IsCorrect ? existingByClientAttempt.CreatedAt : null,
            LastAttemptAt = existingByClientAttempt.CreatedAt
        };

        return Results.Ok(ToResult(existingByClientAttempt, existingVersion.ExplanationJson, existingProgress));
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

    var oldAttempts = await db.Attempts
        .Where(x => x.UserId == userId.Value && x.TaskId == task.Id)
        .ToListAsync();
    if (oldAttempts.Count > 0)
    {
        db.Attempts.RemoveRange(oldAttempts);
    }

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
            TaskId = task.Id
        };
        db.Progress.Add(progress);
    }

    progress.Solved = isCorrect;
    progress.BestScore = score;
    progress.BestScorePercent = score * 100m;
    progress.AttemptsCount = 1;
    progress.LastAttemptId = attempt.Id;
    progress.FirstSolvedAt = isCorrect ? now : null;
    progress.LastAttemptAt = now;

    await db.SaveChangesAsync();
    return Results.Ok(ToResult(attempt, version.ExplanationJson, progress));
});

app.MapGet("/api/quiz/me/solutions", [Authorize] async (QuizDbContext db, ClaimsPrincipal user, string? sectionCode) =>
{
    var userId = TryGetUserId(user);
    if (!userId.HasValue) return Results.Unauthorized();

    var taskQuery = db.Tasks.AsNoTracking().Where(x => x.IsPublished).AsQueryable();
    if (!string.IsNullOrWhiteSpace(sectionCode))
    {
        taskQuery = taskQuery.Where(x => x.SectionCode == sectionCode);
    }

    var tasks = await taskQuery.ToListAsync();
    var taskIds = tasks.Select(x => x.Id).ToList();
    if (taskIds.Count == 0) return Results.Ok(Array.Empty<QuizSolutionDto>());

    var attempts = await db.Attempts.AsNoTracking()
        .Where(x => x.UserId == userId.Value && taskIds.Contains(x.TaskId))
        .OrderByDescending(x => x.CreatedAt)
        .ToListAsync();

    var latestAttempts = attempts
        .GroupBy(x => x.TaskId)
        .Select(x => x.First())
        .OrderByDescending(x => x.CreatedAt)
        .ToList();

    if (latestAttempts.Count == 0) return Results.Ok(Array.Empty<QuizSolutionDto>());

    var versionIds = latestAttempts.Select(x => x.TaskVersionId).Distinct().ToList();
    var versions = await db.TaskVersions.AsNoTracking()
        .Where(x => versionIds.Contains(x.Id))
        .ToDictionaryAsync(x => x.Id);

    var progressByTask = await db.Progress.AsNoTracking()
        .Where(x => x.UserId == userId.Value && taskIds.Contains(x.TaskId))
        .ToDictionaryAsync(x => x.TaskId);

    var tasksById = tasks.ToDictionary(x => x.Id);
    var result = new List<QuizSolutionDto>();

    foreach (var attempt in latestAttempts)
    {
        if (!tasksById.TryGetValue(attempt.TaskId, out var task)) continue;
        if (!versions.TryGetValue(attempt.TaskVersionId, out var version)) continue;
        progressByTask.TryGetValue(attempt.TaskId, out var progress);

        var percent = attempt.MaxScore <= 0 ? 0 : Math.Round(attempt.Score / attempt.MaxScore * 100m, 2);
        result.Add(new QuizSolutionDto(
            QuizTaskDto.FromEntity(task),
            attempt.Id,
            attempt.TaskVersionId,
            attempt.AnswerJson,
            attempt.IsCorrect,
            attempt.Score,
            attempt.MaxScore,
            percent,
            version.ExplanationJson,
            attempt.CreatedAt,
            progress == null ? null : QuizProgressDto.FromEntity(progress)));
    }

    return Results.Ok(result);
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

    static string ExtractExplanationText(string explanationJson)
    {
        if (string.IsNullOrWhiteSpace(explanationJson) || explanationJson == "{}") return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(explanationJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String) return root.GetString() ?? string.Empty;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) return text.GetString() ?? string.Empty;
                if (root.TryGetProperty("markdown", out var markdown) && markdown.ValueKind == JsonValueKind.String) return markdown.GetString() ?? string.Empty;
            }
        }
        catch
        {
            return explanationJson;
        }
        return string.Empty;
    }

    var explanationJson = JsonOrDefault(req.Explanation, req.ExplanationJson, "{}");
    if (string.IsNullOrWhiteSpace(ExtractExplanationText(explanationJson)))
    {
        return Results.BadRequest(new { message = "Explanation is required" });
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
        ExplanationJson = explanationJson,
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
