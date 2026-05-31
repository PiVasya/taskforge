using Microsoft.EntityFrameworkCore;
using TaskForge.Files.Api.Data;
using TaskForge.Files.Api.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<FilesDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true)) { using var s = app.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<FilesDbContext>(); app.Logger.LogInformation("Applying EF Core migrations for FilesDbContext..."); await db.Database.MigrateAsync(); app.Logger.LogInformation("EF Core migrations for FilesDbContext applied."); }
else if (builder.Configuration.GetValue("Database:EnsureCreated", false)) { using var s = app.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<FilesDbContext>().Database.EnsureCreatedAsync(); }
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-files-api" }));
app.MapGet("/health/ready", async (FilesDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-files-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-files-api", database = "taskforge_files", status = "files microservice active" }));
app.MapGet("/api/files/schema-owner", () => Results.Ok(new { database = "taskforge_files", ownedEntities = new[] { "StoredFile" } }));
app.MapPost("/api/files/images", async (HttpRequest req, FilesDbContext db) =>
{
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file == null) return Results.BadRequest(new { message = "file is required" });
    var item = new StoredFile { FileName = file.FileName, ContentType = file.ContentType, Size = file.Length, Url = $"/api/files/{Guid.NewGuid()}" };
    db.Files.Add(item);
    await db.SaveChangesAsync();
    return Results.Ok(new { item.Id, item.FileName, item.ContentType, item.Size, url = item.Url });
});
app.MapGet("/api/files", async (FilesDbContext db) => Results.Ok(await db.Files.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync()));
app.MapGet("/api/private-files/{**path}", (string? path) => Results.NotFound(new { message = "File object storage is not wired yet", path }));
app.Run();
