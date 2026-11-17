using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;
using TaskForge.Execution.Api.Services.Interactive;

using TaskForge.Execution.Api.Endpoints;
using static TaskForge.Execution.Api.Services.Mapping.ExecutionApiMappingService;
using static TaskForge.Execution.Api.Services.Results.ExecutionApiResultsService;
using static TaskForge.Execution.Api.Services.Serialization.ExecutionApiSerializationService;

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

builder.Services.AddTaskForgeDebugDiagnostics("execution-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "execution-api");

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TaskForge.Execution.Api.Services.Sql.SqlWorkerDirectory>();
builder.Services.AddSingleton<TaskForge.Execution.Api.Services.Sql.SqlWakeups>();
builder.Services.AddHostedService<TaskForge.Execution.Api.Services.Sql.SqlQueueMaintenance>();
builder.Services.AddHostedService<TaskForge.Execution.Api.Services.Legacy.LegacyExecutionQueueMaintenance>();
builder.Services.AddHostedService<TaskForge.Execution.Api.Services.Sql.SqlRabbitWakeupPublisher>();
builder.Services.AddSingleton<InteractiveSessionRegistry>();
builder.Services.AddDbContext<ExecutionDbContext>(options =>
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
    var ysqlGuardDb = ysqlGuardScope.ServiceProvider.GetRequiredService<ExecutionDbContext>();
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

// Force session-store validation before accepting multi-node traffic.
if (!builder.Configuration.GetValue("Database:MigrationJobOnly", false) &&
    (builder.Configuration.GetValue("Cluster:MultiNode", false) ||
     string.Equals(builder.Configuration["InteractiveSessions:Store"], "redis", StringComparison.OrdinalIgnoreCase)))
{
    _ = app.Services.GetRequiredService<InteractiveSessionRegistry>();
}

app.UseTaskForgeDebugRequestLogging("execution-api");
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<ExecutionDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for ExecutionDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for ExecutionDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var ensureScope = app.Services.CreateScope();
    var db = ensureScope.ServiceProvider.GetRequiredService<ExecutionDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.UseTaskForgeRequestSecurity("execution");

app.MapExecutionApiEndpoints();

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
