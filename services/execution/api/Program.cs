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

builder.Services.AddTaskForgeDebugDiagnostics("execution-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "execution-api");

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TaskForge.Execution.Api.Services.Sql.SqlWorkerDirectory>();
builder.Services.AddSingleton<TaskForge.Execution.Api.Services.Sql.SqlWakeups>();
builder.Services.AddHostedService<TaskForge.Execution.Api.Services.Sql.SqlQueueMaintenance>();
builder.Services.AddHostedService<TaskForge.Execution.Api.Services.Sql.SqlRabbitWakeupPublisher>();
builder.Services.AddSingleton<InteractiveSessionRegistry>();
builder.Services.AddDbContext<ExecutionDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

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

app.Run();
