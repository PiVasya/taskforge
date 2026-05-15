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

app.MapGet("/api/quiz/tasks", async (QuizDbContext db, ClaimsPrincipal user, string? subjectCode, string? examCode, string? sectionCode, string? type, bool includeDraft = false) =>
{
    var query = db.Tasks.AsNoTracking().AsQueryable();
    var canSeeDrafts = includeDraft && CanEditQuiz(user);
    if (!canSeeDrafts) query = query.Where(x => x.IsPublished);
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
    var canSeeDrafts = CanEditQuiz(user);
    var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => isGuid ? x.Id == id : x.Slug == idOrSlug);
    if (task == null || (!task.IsPublished && !canSeeDrafts)) return Results.NotFound();

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

app.MapGet("/api/admin/quiz/tasks", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, string? subjectCode, string? examCode, string? sectionCode, string? type) =>
{
    var query = db.Tasks.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(subjectCode)) query = query.Where(x => x.SubjectCode == subjectCode);
    if (!string.IsNullOrWhiteSpace(examCode)) query = query.Where(x => x.ExamCode == examCode);
    if (!string.IsNullOrWhiteSpace(sectionCode)) query = query.Where(x => x.SectionCode == sectionCode);
    if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.Type == type);

    var tasks = await query
        .OrderBy(x => x.SectionCode)
        .ThenBy(x => x.Difficulty)
        .ThenBy(x => x.Title)
        .ToListAsync();

    var versions = await LoadCurrentVersionsAsync(db, tasks);
    return Results.Ok(tasks.Select(task => ToAdminDto(task, versions.GetValueOrDefault(task.Id))).ToList());
});

app.MapGet("/api/admin/quiz/tasks/{id:guid}", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, Guid id) =>
{
    var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (task == null) return Results.NotFound(new { message = "Task not found" });
    var version = await db.TaskVersions.AsNoTracking()
        .Where(x => x.TaskId == task.Id && x.VersionNumber == task.CurrentVersion)
        .FirstOrDefaultAsync();
    if (version == null) return Results.NotFound(new { message = "Task version not found" });
    return Results.Ok(ToAdminDto(task, version));
});

app.MapPost("/api/admin/quiz/tasks", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, [FromBody] CreateQuizTaskRequest req) =>
{
    var validation = ValidateTaskRequest(req);
    if (validation != null) return validation;

    var exists = await db.Tasks.AnyAsync(x => x.Slug == req.Slug.Trim());
    if (exists) return Results.Conflict(new { message = "Task slug already exists" });

    var explanationJson = JsonOrDefault(req.Explanation, req.ExplanationJson, "{}");
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

    return Results.Ok(ToAdminDto(task, version));
});

app.MapPut("/api/admin/quiz/tasks/{id:guid}", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, Guid id, [FromBody] CreateQuizTaskRequest req) =>
{
    var validation = ValidateTaskRequest(req);
    if (validation != null) return validation;

    var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id);
    if (task == null) return Results.NotFound(new { message = "Task not found" });

    var newSlug = req.Slug.Trim();
    var duplicate = await db.Tasks.AnyAsync(x => x.Id != id && x.Slug == newSlug);
    if (duplicate) return Results.Conflict(new { message = "Task slug already exists" });

    task.Slug = newSlug;
    task.Type = string.IsNullOrWhiteSpace(req.Type) ? "single-choice" : req.Type.Trim();
    task.Title = req.Title.Trim();
    task.Prompt = req.Prompt.Trim();
    task.SubjectCode = string.IsNullOrWhiteSpace(req.SubjectCode) ? "russian" : req.SubjectCode.Trim();
    task.ExamCode = string.IsNullOrWhiteSpace(req.ExamCode) ? "ct-ce-2026" : req.ExamCode.Trim();
    task.SectionCode = req.SectionCode?.Trim();
    task.Difficulty = req.Difficulty <= 0 ? 1 : req.Difficulty;
    task.TagsJson = JsonOrDefault(req.Tags, req.TagsJson, "[]");
    task.SourceName = req.SourceName;
    task.SourceYear = req.SourceYear;
    task.IsPublished = req.IsPublished;
    task.UpdatedAt = DateTime.UtcNow;

    var nextVersionNumber = await db.TaskVersions
        .Where(x => x.TaskId == task.Id)
        .Select(x => (int?)x.VersionNumber)
        .MaxAsync() ?? 0;
    nextVersionNumber += 1;
    task.CurrentVersion = nextVersionNumber;

    var version = new QuizTaskVersion
    {
        TaskId = task.Id,
        VersionNumber = nextVersionNumber,
        DataJson = JsonOrDefault(req.Data, req.DataJson, "{}"),
        CorrectAnswerJson = JsonOrDefault(req.CorrectAnswer, req.CorrectAnswerJson, "{}"),
        ExplanationJson = JsonOrDefault(req.Explanation, req.ExplanationJson, "{}"),
        ChangeComment = "Edited from CT editor"
    };

    db.TaskVersions.Add(version);
    await db.SaveChangesAsync();

    return Results.Ok(ToAdminDto(task, version));
});


app.MapDelete("/api/admin/quiz/tasks/by-section", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, string? subjectCode, string? examCode, string? sectionCode) =>
{
    if (string.IsNullOrWhiteSpace(sectionCode))
    {
        return Results.BadRequest(new { message = "sectionCode is required" });
    }

    var normalizedSectionCode = sectionCode.Trim().ToUpperInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedSectionCode, "^[AB][0-9]+$"))
    {
        return Results.BadRequest(new { message = "sectionCode must look like A1, A31, B1 or B11" });
    }

    var query = db.Tasks.Where(x => x.SectionCode == normalizedSectionCode).AsQueryable();
    if (!string.IsNullOrWhiteSpace(subjectCode)) query = query.Where(x => x.SubjectCode == subjectCode.Trim());
    if (!string.IsNullOrWhiteSpace(examCode)) query = query.Where(x => x.ExamCode == examCode.Trim());

    var tasks = await query.ToListAsync();
    var taskIds = tasks.Select(x => x.Id).ToList();
    if (taskIds.Count == 0)
    {
        return Results.Ok(new
        {
            sectionCode = normalizedSectionCode,
            tasksDeleted = 0,
            versionsDeleted = 0,
            attemptsDeleted = 0,
            progressDeleted = 0
        });
    }

    var attempts = await db.Attempts.Where(x => taskIds.Contains(x.TaskId)).ToListAsync();
    var progress = await db.Progress.Where(x => taskIds.Contains(x.TaskId)).ToListAsync();
    var versions = await db.TaskVersions.Where(x => taskIds.Contains(x.TaskId)).ToListAsync();

    db.Attempts.RemoveRange(attempts);
    db.Progress.RemoveRange(progress);
    db.TaskVersions.RemoveRange(versions);
    db.Tasks.RemoveRange(tasks);
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        sectionCode = normalizedSectionCode,
        tasksDeleted = tasks.Count,
        versionsDeleted = versions.Count,
        attemptsDeleted = attempts.Count,
        progressDeleted = progress.Count
    });
});

app.MapDelete("/api/admin/quiz/tasks/{id:guid}", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, Guid id) =>
{
    var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id);
    if (task == null) return Results.NotFound(new { message = "Task not found" });

    var attempts = await db.Attempts.Where(x => x.TaskId == id).ToListAsync();
    var progress = await db.Progress.Where(x => x.TaskId == id).ToListAsync();
    var versions = await db.TaskVersions.Where(x => x.TaskId == id).ToListAsync();

    db.Attempts.RemoveRange(attempts);
    db.Progress.RemoveRange(progress);
    db.TaskVersions.RemoveRange(versions);
    db.Tasks.Remove(task);
    await db.SaveChangesAsync();

    return Results.NoContent();
});

app.Run();

static bool CanEditQuiz(ClaimsPrincipal user)
{
    return user.IsInRole("Admin") || user.IsInRole("LearningEditor");
}

static IResult? ValidateTaskRequest(CreateQuizTaskRequest req)
{
    if (string.IsNullOrWhiteSpace(req.Slug) || string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Prompt))
    {
        return Results.BadRequest(new { message = "Slug, Title and Prompt are required" });
    }

    var explanationJson = JsonOrDefault(req.Explanation, req.ExplanationJson, "{}");
    if (string.IsNullOrWhiteSpace(ExtractExplanationText(explanationJson)))
    {
        return Results.BadRequest(new { message = "Explanation is required" });
    }

    var correctAnswerJson = JsonOrDefault(req.CorrectAnswer, req.CorrectAnswerJson, "{}");
    if (string.IsNullOrWhiteSpace(ExtractAnswerText(correctAnswerJson)))
    {
        return Results.BadRequest(new { message = "Correct answer is required" });
    }

    return null;
}

static async Task<Dictionary<Guid, QuizTaskVersion?>> LoadCurrentVersionsAsync(QuizDbContext db, IReadOnlyCollection<QuizTask> tasks)
{
    var taskIds = tasks.Select(x => x.Id).ToList();
    if (taskIds.Count == 0) return new Dictionary<Guid, QuizTaskVersion?>();

    var versions = await db.TaskVersions.AsNoTracking()
        .Where(x => taskIds.Contains(x.TaskId))
        .ToListAsync();

    return tasks.ToDictionary(
        task => task.Id,
        task => versions.FirstOrDefault(version => version.TaskId == task.Id && version.VersionNumber == task.CurrentVersion));
}

static AdminQuizTaskDetailsDto ToAdminDto(QuizTask task, QuizTaskVersion? version)
{
    return new AdminQuizTaskDetailsDto(
        QuizTaskDto.FromEntity(task),
        version?.Id ?? Guid.Empty,
        version?.VersionNumber ?? task.CurrentVersion,
        version?.DataJson ?? "{}",
        version?.CorrectAnswerJson ?? "{}",
        version?.ExplanationJson ?? "{}");
}

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

static string JsonOrDefault(JsonElement? element, string? json, string fallback)
{
    if (!string.IsNullOrWhiteSpace(json)) return json;
    return element.HasValue && element.Value.ValueKind != JsonValueKind.Undefined ? element.Value.GetRawText() : fallback;
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

static string ExtractAnswerText(string answerJson)
{
    if (string.IsNullOrWhiteSpace(answerJson) || answerJson == "{}") return string.Empty;
    try
    {
        using var doc = JsonDocument.Parse(answerJson);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.String) return root.GetString() ?? string.Empty;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
            if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) return text.GetString() ?? string.Empty;
            if (root.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.Array)
            {
                return string.Join(',', selected.EnumerateArray().Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)));
            }
        }
    }
    catch
    {
        return answerJson;
    }
    return string.Empty;
}
