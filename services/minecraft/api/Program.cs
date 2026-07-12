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

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MinecraftDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for MinecraftDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for MinecraftDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MinecraftDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseTaskForgeRequestSecurity("minecraft");
app.MapMinecraftApiEndpoints();
app.Run();
