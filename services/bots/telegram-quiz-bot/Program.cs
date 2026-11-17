using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Bot;
using TelegramQuizBot.Configuration;
using TelegramQuizBot.Data;
using TelegramQuizBot.Import;
using TelegramQuizBot.Integrations;
using TelegramQuizBot.Services;
using TelegramQuizBot.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("telegram-quiz-bot");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "telegram-quiz-bot");

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
builder.Services.AddScoped<StudentDirectoryService>();
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

// The standalone Telegram quiz bot is preserved, but its K3s database is YSQL.
if (builder.Configuration.GetValue("Database:RequireYugabyte", false))
{
    using var ysqlGuardScope = app.Services.CreateScope();
    var ysqlGuardDb = ysqlGuardScope.ServiceProvider.GetRequiredService<TelegramQuizDbContext>();
    var ysqlConnection = ysqlGuardDb.Database.GetDbConnection();
    await ysqlConnection.OpenAsync();
    try
    {
        await using var ysqlCommand = ysqlConnection.CreateCommand();
        ysqlCommand.CommandText = "SELECT version()";
        var ysqlVersion = (await ysqlCommand.ExecuteScalarAsync())?.ToString();
        if (ysqlVersion is null || !ysqlVersion.Contains("-YB-", global::System.StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("K3s Telegram quiz bot requires YugabyteDB YSQL.");
    }
    finally { await ysqlConnection.CloseAsync(); }
}

app.UseTaskForgeDebugRequestLogging("telegram-quiz-bot");

if (args.Length >= 2 && args[0] == "--import-old-sqlite")
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TelegramQuizDbContext>();
    await db.Database.MigrateAsync();
    var importer = scope.ServiceProvider.GetRequiredService<LegacySqliteImportService>();
    await importer.ImportAsync(args[1], CancellationToken.None);
    return;
}

app.MapGet("/health", () => Microsoft.AspNetCore.Http.Results.Ok(new
{
    service = "taskforge-telegram-quiz-bot",
    status = "ok",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/ready", async (IServiceProvider services, IConfiguration configuration, CancellationToken ct) =>
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TelegramQuizDbContext>();
    var canConnect = await db.Database.CanConnectAsync(ct);
    var telegram = configuration.GetSection("TelegramQuiz").Get<TelegramQuizOptions>() ?? new TelegramQuizOptions();

    return canConnect
        ? Microsoft.AspNetCore.Http.Results.Ok(new
        {
            service = "taskforge-telegram-quiz-bot",
            status = "ready",
            database = "ok",
            teacherBotConfigured = !string.IsNullOrWhiteSpace(telegram.TeacherBotToken),
            studentBotConfigured = !string.IsNullOrWhiteSpace(telegram.StudentBotToken),
            utc = DateTimeOffset.UtcNow
        })
        : Microsoft.AspNetCore.Http.Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

// K3s migration Job migrates this bot DB without starting Telegram polling.
if (builder.Configuration.GetValue("Database:MigrationJobOnly", false))
{
    using var migrationScope = app.Services.CreateScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<TelegramQuizDbContext>();
    await migrationDb.Database.MigrateAsync();
    app.Logger.LogInformation("Telegram quiz bot migrations completed; exiting without polling.");
}
else
{
    app.Run();
}
