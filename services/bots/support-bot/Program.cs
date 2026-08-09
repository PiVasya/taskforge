using System.Security.Cryptography;
using System.Text;
using TaskForge.SupportBot;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("support-bot");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "support-bot");
builder.Services.AddHttpClient("support-api", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri((cfg["SupportApi:BaseUrl"] ?? cfg["Services:SupportApi"] ?? "http://support-api:8080").TrimEnd('/') + "/");
});
builder.Services.AddHttpClient("identity-api", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri((cfg["IdentityApi:BaseUrl"] ?? cfg["Services:IdentityApi"] ?? "http://identity-api:8080").TrimEnd('/') + "/");
});
builder.Services.AddSingleton<AiAccessDigestAggregator>();
builder.Services.AddHostedService<GatewayAiAccessLogIngestor>();
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService<Worker>(sp => sp.GetRequiredService<Worker>());

var app = builder.Build();

app.MapGet("/health/ready", (Worker worker) => Results.Ok(new
{
    status = "ready",
    service = "taskforge-support-bot",
    telegramReady = worker.TelegramReady,
    aiAccessTelemetryEnabled = app.Configuration.GetValue("AiAccessTelemetry:Enabled", true),
    aiAccessPending = app.Services.GetRequiredService<AiAccessDigestAggregator>().PendingCount
}));

app.MapPost("/api/internal/password-recovery/send", async (
    PasswordRecoveryDeliveryRequest request,
    HttpContext http,
    IConfiguration cfg,
    Worker worker,
    CancellationToken ct) =>
{
    if (!InternalRequestAuthorized(http, cfg))
    {
        return Results.NotFound(new
        {
            status = 404,
            code = "NOT_FOUND",
            message = "Ресурс не найден.",
            severity = "warning"
        });
    }

    if (request.TelegramChatId == 0 ||
        string.IsNullOrWhiteSpace(request.VerificationCode) ||
        request.VerificationCode.Length != 8 ||
        request.VerificationCode.Any(ch => !char.IsDigit(ch)))
    {
        return Results.BadRequest(new
        {
            delivered = false,
            code = "INVALID_RECOVERY_DELIVERY_REQUEST",
            message = "Некорректный запрос доставки кода восстановления."
        });
    }

    var result = await worker.SendPasswordRecoveryCodeAsync(request, ct);
    if (result.Delivered)
    {
        return Results.Ok(new { delivered = true });
    }

    var statusCode = result.Code == "TELEGRAM_NOT_READY"
        ? StatusCodes.Status503ServiceUnavailable
        : StatusCodes.Status502BadGateway;

    return Results.Json(new
    {
        delivered = false,
        code = result.Code,
        message = result.Message
    }, statusCode: statusCode);
});

app.MapPost("/api/internal/ai-access/events", (
    AiAccessEventBatch batch,
    HttpContext http,
    IConfiguration cfg,
    AiAccessDigestAggregator aggregator) =>
{
    if (!InternalRequestAuthorized(http, cfg))
    {
        return Results.NotFound(new
        {
            status = 404,
            code = "NOT_FOUND",
            message = "Ресурс не найден.",
            severity = "warning"
        });
    }

    if (batch.Events is null || batch.Events.Count == 0 || batch.Events.Count > 100)
    {
        return Results.BadRequest(new
        {
            accepted = false,
            code = "INVALID_AI_ACCESS_EVENT_BATCH",
            message = "Пакет AI telemetry должен содержать от 1 до 100 событий."
        });
    }

    aggregator.Record(batch.Events);
    return Results.Ok(new
    {
        accepted = true,
        count = batch.Events.Count,
        pending = aggregator.PendingCount
    });
});

app.Run();

static bool InternalRequestAuthorized(HttpContext http, IConfiguration cfg)
{
    var expected = FirstNonEmpty(
        cfg["InternalApi:Key"],
        cfg["TaskForgeInternalApi:ApiKey"],
        cfg["TaskForge:InternalKey"],
        Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY"));
    var supplied = http.Request.Headers["X-Internal-Key"].ToString();

    if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied))
    {
        return false;
    }

    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    return expectedBytes.Length == suppliedBytes.Length &&
           CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
}

static string? FirstNonEmpty(params string?[] values)
    => values.Select(value => (value ?? string.Empty).Trim()).FirstOrDefault(value => value.Length > 0);
