using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Hubs;
using TaskForge.Realtime;

using TaskForge.Tasks.Api.Endpoints;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

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

builder.Services.AddTaskForgeDebugDiagnostics("tasks-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "tasks-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddTaskForgeSignalR(builder.Configuration, "tasks-api");
builder.Services.AddHttpClient();
builder.Services.AddHostedService<TaskForge.Tasks.Api.Services.Transfer.GraphTransferWorker>();
builder.Services.AddSingleton<AdminSolutionEventPublisher>();
builder.Services.AddHostedService<TaskForge.Tasks.Api.Services.Sql.SqlValidationDispatcher>();
builder.Services.AddDbContext<TasksDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
    if (builder.Configuration.GetValue("Database:RequireYugabyte", false) &&
        builder.Configuration.GetValue("Database:MigrationJobOnly", false))
        options.ReplaceService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository,
            TaskForge.Yugabyte.SingleJobYugabyteHistoryRepository>();
});
builder.Services.AddScoped<TaskForge.Tasks.Api.Services.Access.CourseMapProjectionService>();
var app = builder.Build();

// Explicit, opt-in protection for the new K3s environment: never silently send
// its writes (or EF schema migrations) to the legacy PostgreSQL server.
if (builder.Configuration.GetValue("Database:RequireYugabyte", false))
{
    using var ysqlGuardScope = app.Services.CreateScope();
    var ysqlGuardDb = ysqlGuardScope.ServiceProvider.GetRequiredService<TasksDbContext>();
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

using (var criticalServiceScope = app.Services.CreateScope())
{
    _ = criticalServiceScope.ServiceProvider.GetRequiredService<TaskForge.Tasks.Api.Services.Access.CourseMapProjectionService>();
}

app.UseTaskForgeDebugRequestLogging("tasks-api");

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<TasksDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for TasksDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for TasksDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var ensureScope = app.Services.CreateScope();
    await ensureScope.ServiceProvider.GetRequiredService<TasksDbContext>().Database.EnsureCreatedAsync();
}

// Startup backfill/cleanup is a one-off maintenance job in multi-node mode.
if (!builder.Configuration.GetValue("Cluster:MultiNode", false) || builder.Configuration.GetValue("Database:MigrationJobOnly", false))
{
    using var assignmentSpecScope = app.Services.CreateScope();
    var db = assignmentSpecScope.ServiceProvider.GetRequiredService<TasksDbContext>();
    await TaskForge.Tasks.Api.Services.Specs.AssignmentSpecBackfillService.BackfillAsync(db);
    await TaskForge.Tasks.Api.Services.Testing.TestAttemptStorageCleanupService.CleanupAsync(db, app.Logger);
}

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("tasks");

app.MapAssignmentApiEndpoints();
app.MapHub<AssignmentAnalyticsHub>("/hubs/assignment-analytics");

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
