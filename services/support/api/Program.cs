using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;
using TaskForge.Support.Api.Domain;

using TaskForge.Support.Api.Endpoints;
using static TaskForge.Support.Api.Services.Common.SupportApiCommonService;
using static TaskForge.Support.Api.Services.Mapping.SupportApiMappingService;
using static TaskForge.Support.Api.Services.Serialization.SupportApiSerializationService;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("support-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "support-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<SupportDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("support-api");
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true)) { using var s = app.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<SupportDbContext>(); app.Logger.LogInformation("Applying EF Core migrations for SupportDbContext..."); await db.Database.MigrateAsync(); app.Logger.LogInformation("EF Core migrations for SupportDbContext applied."); }
else if (builder.Configuration.GetValue("Database:EnsureCreated", false)) { using var s = app.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<SupportDbContext>().Database.EnsureCreatedAsync(); }
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("support");

app.MapSupportApiEndpoints();

app.Run();
