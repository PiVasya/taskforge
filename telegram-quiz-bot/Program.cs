using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TelegramQuizBot.Bot;
using TelegramQuizBot.Configuration;
using TelegramQuizBot.Data;
using TelegramQuizBot.Import;
using TelegramQuizBot.Integrations;
using TelegramQuizBot.Services;
using TelegramQuizBot.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TelegramQuizOptions>(builder.Configuration.GetSection("TelegramQuiz"));
builder.Services.Configure<S3Options>(builder.Configuration.GetSection("S3"));
builder.Services.Configure<TaskForgeOptions>(builder.Configuration.GetSection("TaskForge"));

builder.Services.AddDbContext<TelegramQuizDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("TelegramQuizDb")));

builder.Services.AddHttpClient<TaskForgeApiClient>();

builder.Services.AddSingleton<TeacherBotStateStore>();
builder.Services.AddSingleton<StudentBotStateStore>();
builder.Services.AddSingleton<TelegramBotClientFactory>();
builder.Services.AddSingleton<IS3ImageStorage, S3ImageStorage>();

builder.Services.AddScoped<TeacherAccessService>();
builder.Services.AddScoped<StudentAccessService>();
builder.Services.AddScoped<CategoryService>();
builder.Services.AddScoped<QuizService>();
builder.Services.AddScoped<ProgressService>();
builder.Services.AddScoped<TechnicalBreakService>();
builder.Services.AddScoped<StatisticsService>();
builder.Services.AddScoped<LegacySqliteImportService>();

builder.Services.AddHostedService<DatabaseStartupService>();
builder.Services.AddHostedService<TeacherBotHostedService>();
builder.Services.AddHostedService<StudentBotHostedService>();

var app = builder.Build();

if (args.Length >= 2 && args[0] == "--import-old-sqlite")
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TelegramQuizDbContext>();
    await db.Database.MigrateAsync();
    var importer = scope.ServiceProvider.GetRequiredService<LegacySqliteImportService>();
    await importer.ImportAsync(args[1], CancellationToken.None);
    return;
}

app.MapGet("/health", () => Results.Ok(new
{
    service = "taskforge-telegram-quiz-bot",
    status = "ok",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/ready", async (IServiceScopeFactory scopeFactory, IOptions<TelegramQuizOptions> options, CancellationToken ct) =>
{
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TelegramQuizDbContext>();
    var databaseReady = await db.Database.CanConnectAsync(ct);
    var telegramOptions = options.Value;

    var payload = new
    {
        service = "taskforge-telegram-quiz-bot",
        status = databaseReady ? "ready" : "degraded",
        database = databaseReady ? "ok" : "unavailable",
        teacherBotConfigured = !string.IsNullOrWhiteSpace(telegramOptions.TeacherBotToken),
        studentBotConfigured = !string.IsNullOrWhiteSpace(telegramOptions.StudentBotToken),
        utc = DateTimeOffset.UtcNow
    };

    return databaseReady
        ? Results.Ok(payload)
        : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();
