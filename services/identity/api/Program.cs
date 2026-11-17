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
using TaskForge.Identity.Api.Services.AccountLifecycle;
using TaskForge.Identity.Api.Services.Security;
using TaskForge.Identity.Api.Services.Telemetry;

using TaskForge.Identity.Api.Endpoints;
using static TaskForge.Identity.Api.Services.Access.IdentityApiAccessService;
using static TaskForge.Identity.Api.Services.Common.IdentityApiCommonService;
using static TaskForge.Identity.Api.Services.Image.IdentityApiImageService;
using static TaskForge.Identity.Api.Services.Mapping.IdentityApiMappingService;
using static TaskForge.Identity.Api.Services.Results.IdentityApiResultsService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

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

builder.Services.AddTaskForgeDebugDiagnostics("identity-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "identity-api");

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("support-bot", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri((cfg["Services:SupportBot"] ?? cfg["AiAccessTelemetry:SupportBotBaseUrl"] ?? "http://support-bot:8080").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddSingleton<AiAccessTelemetryClient>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AiAccessTelemetryClient>());
builder.Services.AddSingleton<IdentityAuthRateLimiter>();
builder.Services.AddHostedService<BlockedAccountCacheSynchronizer>();
builder.Services.AddDbContext<IdentityDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
    if (builder.Configuration.GetValue("Database:RequireYugabyte", false) &&
        builder.Configuration.GetValue("Database:MigrationJobOnly", false))
        options.ReplaceService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository,
            TaskForge.Yugabyte.SingleJobYugabyteHistoryRepository>();
});

var app = builder.Build();

// Explicit, opt-in protection for the new K3s environment: never silently send
// its writes (or EF schema migrations) to the legacy PostgreSQL server.
if (builder.Configuration.GetValue("Database:RequireYugabyte", false))
{
    using var ysqlGuardScope = app.Services.CreateScope();
    var ysqlGuardDb = ysqlGuardScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
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

app.UseTaskForgeDebugRequestLogging("identity-api");

ValidateProductionIdentityConfig(app.Configuration, app.Environment);

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for IdentityDbContext...");
    await db.Database.MigrateAsync();
    await BackfillUserLoginsAsync(db, app.Logger);
    await SeedFeatureRoles(db);
    app.Logger.LogInformation("EF Core migrations for IdentityDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var ensureScope = app.Services.CreateScope();
    var db = ensureScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseTaskForgeRequestSecurity("identity");

app.MapIdentityApiEndpoints();

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
