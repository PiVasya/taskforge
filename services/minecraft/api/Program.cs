using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddDbContext<MinecraftDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true)) { using var s = app.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<MinecraftDbContext>(); app.Logger.LogInformation("Applying EF Core migrations for MinecraftDbContext..."); await db.Database.MigrateAsync(); app.Logger.LogInformation("EF Core migrations for MinecraftDbContext applied."); }
else if (builder.Configuration.GetValue("Database:EnsureCreated", false)) { using var s = app.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<MinecraftDbContext>().Database.EnsureCreatedAsync(); }
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-minecraft-api" }));
app.MapGet("/health/ready", async (MinecraftDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-minecraft-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-minecraft-api", database = "taskforge_minecraft", status = "minecraft microservice active" }));
app.MapGet("/api/minecraft/schema-owner", () => Results.Ok(new { database = "taskforge_minecraft", ownedEntities = new[] { "MinecraftLink", "MinecraftChatMessage" } }));
app.MapGet("/api/integrations/minecraft/status", async (MinecraftDbContext db) => Results.Ok(new { linked = await db.Links.AnyAsync(x => x.Confirmed), playerName = (string?)null }));
app.MapPost("/api/integrations/minecraft/request", async (MinecraftDbContext db) => { var link = new MinecraftLink { Code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant() }; db.Links.Add(link); await db.SaveChangesAsync(); return Results.Ok(new { code = link.Code, expiresInSeconds = 600 }); });
app.MapPost("/api/integrations/minecraft/confirm", async (MinecraftConfirmRequest req, MinecraftDbContext db) => { var link = await db.Links.FirstOrDefaultAsync(x => x.Code == req.Code); if (link == null) return Results.NotFound(); link.Confirmed = true; link.PlayerName = req.PlayerName; link.PlayerUuid = req.PlayerUuid; await db.SaveChangesAsync(); return Results.Ok(new { linked = true, link.PlayerName }); });
app.MapDelete("/api/integrations/minecraft/unlink", async (MinecraftDbContext db) => { db.Links.RemoveRange(await db.Links.ToListAsync()); await db.SaveChangesAsync(); return Results.Ok(new { linked = false }); });
app.MapGet("/api/admin/minecraft-links", async (MinecraftDbContext db) => Results.Ok(await db.Links.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync()));
app.MapGet("/api/integrations/minecraft/chat/meta", () => Results.Ok(new { enabled = true, maxLength = 500 }));
app.MapGet("/api/integrations/minecraft/chat/messages", async (MinecraftDbContext db) => Results.Ok(await db.ChatMessages.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).OrderBy(x => x.CreatedAt).ToListAsync()));
app.MapPost("/api/integrations/minecraft/chat/messages", async (MinecraftChatRequest req, MinecraftDbContext db) => { var msg = new MinecraftChatMessage { Author = string.IsNullOrWhiteSpace(req.Author) ? "web" : req.Author!, Text = req.Text ?? string.Empty }; db.ChatMessages.Add(msg); await db.SaveChangesAsync(); return Results.Ok(msg); });
app.MapHub<MinecraftChatHub>("/hubs/minecraft-chat");
app.Run();
public sealed record MinecraftConfirmRequest(string? Code, string? PlayerName, string? PlayerUuid);
public sealed record MinecraftChatRequest(string? Author, string? Text);
public sealed class MinecraftChatHub : Hub { }
