using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;
using TaskForge.Support.Api.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddDbContext<SupportDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true)) { using var s = app.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<SupportDbContext>(); app.Logger.LogInformation("Applying EF Core migrations for SupportDbContext..."); await db.Database.MigrateAsync(); app.Logger.LogInformation("EF Core migrations for SupportDbContext applied."); }
else if (builder.Configuration.GetValue("Database:EnsureCreated", false)) { using var s = app.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<SupportDbContext>().Database.EnsureCreatedAsync(); }
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-support-api" }));
app.MapGet("/health/ready", async (SupportDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-support-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-support-api", database = "taskforge_support", status = "support microservice active" }));
app.MapGet("/api/support/schema-owner", () => Results.Ok(new { database = "taskforge_support", ownedEntities = new[] { "SupportTicket", "SupportMessage" } }));
app.MapGet("/api/support", async (SupportDbContext db) => Results.Ok((await db.Tickets.AsNoTracking().OrderByDescending(x => x.UpdatedAt).ToListAsync()).Select(ToDto).ToList()));
app.MapPost("/api/support", async (SupportRequest req, SupportDbContext db) => { var t = new SupportTicket { Subject = string.IsNullOrWhiteSpace(req.Subject) ? "Обращение" : req.Subject.Trim() }; db.Tickets.Add(t); db.Messages.Add(new SupportMessage { TicketId = t.Id, Text = req.Message ?? req.Text ?? string.Empty }); await db.SaveChangesAsync(); return Results.Ok(ToDto(t)); });
app.MapGet("/api/support/{ticketId:guid}", async (Guid ticketId, SupportDbContext db) => { var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId); if (t == null) return Results.NotFound(); var messages = await db.Messages.AsNoTracking().Where(x => x.TicketId == ticketId).OrderBy(x => x.CreatedAt).ToListAsync(); return Results.Ok(new { ticket = ToDto(t), messages }); });
app.MapPost("/api/support/{ticketId:guid}", async (Guid ticketId, SupportRequest req, SupportDbContext db) => { var t = await db.Tickets.FindAsync(ticketId); if (t == null) return Results.NotFound(); t.UpdatedAt = DateTimeOffset.UtcNow; db.Messages.Add(new SupportMessage { TicketId = ticketId, Text = req.Message ?? req.Text ?? string.Empty }); await db.SaveChangesAsync(); return Results.Ok(ToDto(t)); });
app.MapGet("/api/telegram/status", () => Results.Ok(new { configured = false }));
app.MapHub<SupportHub>("/hubs/support");
app.Run();
static object ToDto(SupportTicket x) => new { x.Id, x.Subject, x.Status, x.UserId, x.CreatedAt, x.UpdatedAt };
public sealed record SupportRequest(string? Subject, string? Message, string? Text);
public sealed class SupportHub : Hub { }
