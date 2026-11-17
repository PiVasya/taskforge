using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Services.AccountIntelligence;
using TaskForge.Ai.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// This service retains only deterministic account investigation and lifecycle operations.
// The legacy database model remains intact until an operator-generated EF migration.
if (builder.Configuration.GetValue("Cluster:MultiNode", false) &&
    !builder.Configuration.GetValue("Database:MigrationJobOnly", false) &&
    (builder.Configuration.GetValue("Database:MigrateOnStartup", true) ||
     builder.Configuration.GetValue("Database:EnsureCreated", false)))
{
    throw new InvalidOperationException(
        "Multi-node API replicas must not run automatic database migrations or EnsureCreated. " +
        "Set Database__MigrateOnStartup=false and Database__EnsureCreated=false; use a dedicated migration job.");
}

builder.Services.AddTaskForgeDebugDiagnostics("ai-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "ai-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AccountIntelligenceScanner>();
builder.Services.AddSingleton<AccountLifecycleCoordinator>();
builder.Services.AddHostedService<AccountIntelligenceWorker>();
builder.Services.AddHostedService<AccountLifecycleWorker>();
builder.Services.AddDbContext<AiDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

// Explicit, opt-in protection for the new K3s environment: never silently send
// its writes (or EF schema migrations) to the legacy PostgreSQL server.
if (builder.Configuration.GetValue("Database:RequireYugabyte", false))
{
    using var ysqlGuardScope = app.Services.CreateScope();
    var ysqlGuardDb = ysqlGuardScope.ServiceProvider.GetRequiredService<AiDbContext>();
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
app.UseTaskForgeDebugRequestLogging("ai-api");

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<AiDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for AiDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for AiDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var ensureScope = app.Services.CreateScope();
    var db = ensureScope.ServiceProvider.GetRequiredService<AiDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("ai");
app.MapAiApiEndpoints();
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
