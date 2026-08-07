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

using TaskForge.Identity.Api.Endpoints;
using static TaskForge.Identity.Api.Services.Access.IdentityApiAccessService;
using static TaskForge.Identity.Api.Services.Common.IdentityApiCommonService;
using static TaskForge.Identity.Api.Services.Image.IdentityApiImageService;
using static TaskForge.Identity.Api.Services.Mapping.IdentityApiMappingService;
using static TaskForge.Identity.Api.Services.Results.IdentityApiResultsService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("identity-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "identity-api");

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IdentityAuthRateLimiter>();
builder.Services.AddHostedService<BlockedAccountCacheSynchronizer>();
builder.Services.AddDbContext<IdentityDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

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

app.Run();
