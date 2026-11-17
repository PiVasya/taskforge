using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Endpoints;

using TaskForge.Cluster;

var builder = WebApplication.CreateBuilder(args);

// Schema migrations and one-off initialization are deployment jobs in a cluster.
// Do not let every API replica race to mutate the schema during startup.
if (builder.Configuration.GetValue("Cluster:MultiNode", false) &&
    !builder.Configuration.GetValue("Database:MigrationJobOnly", false) &&
    (builder.Configuration.GetValue("Database:MigrateOnStartup", true) ||
     builder.Configuration.GetValue("Database:EnsureCreated", false)))
{
    throw new InvalidOperationException(
        "Multi-node API replicas must not run automatic database migrations or EnsureCreated. " +
        "Set Database__MigrateOnStartup=false and Database__EnsureCreated=false; use a dedicated migration job.");
}

builder.Services.AddTaskForgeDebugDiagnostics("minecraft-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "minecraft-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddTaskForgeSignalR(builder.Configuration, "minecraft-api");
builder.Services.AddHttpClient();
builder.Services.AddDbContext<MinecraftDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// Explicit, opt-in protection for the new K3s environment: never silently send
// its writes (or EF schema migrations) to the legacy PostgreSQL server.
if (builder.Configuration.GetValue("Database:RequireYugabyte", false))
{
    using var ysqlGuardScope = app.Services.CreateScope();
    var ysqlGuardDb = ysqlGuardScope.ServiceProvider.GetRequiredService<MinecraftDbContext>();
    var ysqlGuardConnection = ysqlGuardDb.Database.GetDbConnection();
    await ysqlGuardConnection.OpenAsync();
    try
    {
        await using var ysqlGuardCommand = ysqlGuardConnection.CreateCommand();
        ysqlGuardCommand.CommandText = "SELECT version()";
        var ysqlVersion = (await ysqlGuardCommand.ExecuteScalarAsync())?.ToString();
        if (ysqlVersion is null || !ysqlVersion.Contains("-YB-", global::System.StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("K3s requires YugabyteDB YSQL, but the connected engine is not YugabyteDB.");
    }
    finally
    {
        await ysqlGuardConnection.CloseAsync();
    }
}

app.UseTaskForgeDebugRequestLogging("minecraft-api");

var configuredWebhookBaseUrl = (builder.Configuration["MINECRAFT_WEBHOOK_BASE_URL"]
    ?? builder.Configuration["MINECRAFT_SERVER_URL"]
    ?? string.Empty).Trim();
var configuredWebhookPath = (builder.Configuration["MINECRAFT_WEBHOOK_SEND_CODE_PATH"]
    ?? builder.Configuration["MINECRAFT_SERVER_LINK_PATH"]
    ?? "/taskforge/link/send").Trim();
var configuredHealthUrl = (builder.Configuration["MINECRAFT_HEALTH_URL"] ?? string.Empty).Trim();
var configuredWebhookKey = builder.Configuration["MINECRAFT_WEBHOOK_KEY"]
    ?? builder.Configuration["MINECRAFT_SERVER_KEY"]
    ?? string.Empty;
var configuredPluginKey = builder.Configuration["MINECRAFT_PLUGIN_KEY"]
    ?? builder.Configuration["MINECRAFT_SERVER_KEY"]
    ?? string.Empty;

app.Logger.LogInformation(
    "[minecraft-config] webhookBaseUrl={WebhookBaseUrl} webhookPath={WebhookPath} healthUrl={HealthUrl} webhookKeyPresent={WebhookKeyPresent} webhookKeyLen={WebhookKeyLength} webhookKeyFp={WebhookKeyFingerprint} pluginKeyPresent={PluginKeyPresent} pluginKeyLen={PluginKeyLength} pluginKeyFp={PluginKeyFingerprint}",
    string.IsNullOrWhiteSpace(configuredWebhookBaseUrl) ? "<empty>" : configuredWebhookBaseUrl,
    configuredWebhookPath,
    string.IsNullOrWhiteSpace(configuredHealthUrl) ? "<empty>" : configuredHealthUrl,
    !string.IsNullOrWhiteSpace(configuredWebhookKey),
    configuredWebhookKey.Length,
    KeyFingerprint(configuredWebhookKey),
    !string.IsNullOrWhiteSpace(configuredPluginKey),
    configuredPluginKey.Length,
    KeyFingerprint(configuredPluginKey));

if (string.IsNullOrWhiteSpace(configuredWebhookBaseUrl))
    app.Logger.LogError("[minecraft-config] link-code delivery is disabled because MINECRAFT_WEBHOOK_BASE_URL is empty");
if (string.IsNullOrWhiteSpace(configuredWebhookKey))
    app.Logger.LogError("[minecraft-config] link-code delivery is disabled because MINECRAFT_WEBHOOK_KEY is empty");
if (string.IsNullOrWhiteSpace(configuredHealthUrl))
    app.Logger.LogWarning("[minecraft-config] direct Minecraft health URL is empty; online-player probe will use the webhook base URL when possible");

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api/integrations/minecraft"))
    {
        await next();
        return;
    }

    var incomingPluginKey = context.Request.Headers["X-Minecraft-Key"].ToString();
    var incomingTaskForgeKey = context.Request.Headers["X-TaskForge-Key"].ToString();
    var expectedKey = builder.Configuration["MINECRAFT_PLUGIN_KEY"]
        ?? builder.Configuration["MINECRAFT_SERVER_KEY"]
        ?? string.Empty;
    var requestId = context.Request.Headers["X-Request-Id"].ToString();
    var stopwatch = Stopwatch.StartNew();

    app.Logger.LogInformation(
        "[minecraft-integration-http] -> {Method} {Path}{Query} remote={RemoteIp} requestId={RequestId} minecraftKeyPresent={MinecraftKeyPresent} minecraftKeyLen={MinecraftKeyLen} minecraftKeyFp={MinecraftKeyFp} taskForgeKeyPresent={TaskForgeKeyPresent} taskForgeKeyLen={TaskForgeKeyLen} taskForgeKeyFp={TaskForgeKeyFp} expectedKeyPresent={ExpectedKeyPresent} expectedKeyLen={ExpectedKeyLen} expectedKeyFp={ExpectedKeyFp}",
        context.Request.Method,
        context.Request.Path,
        context.Request.QueryString,
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        string.IsNullOrWhiteSpace(requestId) ? "missing" : requestId,
        !string.IsNullOrWhiteSpace(incomingPluginKey),
        incomingPluginKey.Length,
        KeyFingerprint(incomingPluginKey),
        !string.IsNullOrWhiteSpace(incomingTaskForgeKey),
        incomingTaskForgeKey.Length,
        KeyFingerprint(incomingTaskForgeKey),
        !string.IsNullOrWhiteSpace(expectedKey),
        expectedKey.Length,
        KeyFingerprint(expectedKey));

    try
    {
        await next();
    }
    finally
    {
        stopwatch.Stop();
        app.Logger.LogInformation(
            "[minecraft-integration-http] <- {Method} {Path} status={StatusCode} elapsedMs={ElapsedMs} trace={TraceIdentifier}",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            context.TraceIdentifier);
    }
});

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MinecraftDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for MinecraftDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for MinecraftDbContext applied.");
}
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseTaskForgeRequestSecurity("minecraft");
app.MapMinecraftApiEndpoints();
// Kubernetes runs EF migrations in a single-run Job using this same API image.
// The Job executes the existing migration block above and exits before binding
// HTTP ports or starting BackgroundService workers.
if (builder.Configuration.GetValue("Database:MigrationJobOnly", false))
{
    if (!builder.Configuration.GetValue("Database:MigrateOnStartup", true) ||
        builder.Configuration.GetValue("Database:EnsureCreated", false))
        throw new InvalidOperationException("MigrationJobOnly requires MigrateOnStartup=true and EnsureCreated=false.");
    app.Logger.LogInformation("EF migration Job completed; exiting without starting API or hosted workers.");
}
else
{
    app.Run();
}

static string KeyFingerprint(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "missing";
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
}
