using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LearningContentService.Data;
using LearningContentService.Data.Entities;
using LearningContentService.DTO;
using LearningContentService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using LearningContentService.Endpoints;
using static LearningContentService.Services.Common.LearningContentCommonService;
using static LearningContentService.Services.Serialization.LearningContentSerializationService;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("content-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "content-api");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddDbContext<LearningDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("LearningConnection"));
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = ClaimTypes.NameIdentifier,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrWhiteSpace(context.Token) && context.Request.Cookies.TryGetValue("tf_at", out var token))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("content-api");

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled learning-content-service error. Path: {Path}", context.Request.Path);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new
            {
                status = 500,
                message = "Внутренняя ошибка learning-content-service.",
                detail = ex.Message,
                hint = "Открой docker logs taskforge-learning-content-service и проверь эту же ошибку по времени."
            });
        }
    }
});

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.UseTaskForgeRequestSecurity("content");


using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LearningDbContext>();
    var migrateOnStartup = builder.Configuration.GetValue("Database:MigrateOnStartup", true);
    var ensureCreated = builder.Configuration.GetValue("Database:EnsureCreated", false);

    if (migrateOnStartup)
    {
        app.Logger.LogInformation("Applying LearningDbContext migrations...");
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("LearningDbContext migrations applied.");
    }
    else if (ensureCreated)
    {
        await db.Database.EnsureCreatedAsync();
    }

    if (builder.Configuration.GetValue("Seed:InitialCatalog", false))
    {
        await LearningSeedService.SeedInitialCatalogAsync(db);
    }
}

app.MapLearningContentEndpoints();

app.Run();
