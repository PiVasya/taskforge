using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("minecraft-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "minecraft-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<MinecraftDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("minecraft-api");

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
app.Run();

static string KeyFingerprint(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "missing";
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
}
