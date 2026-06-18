using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TaskForge.Files.Api.Data;
using TaskForge.Files.Api.Domain;

using TaskForge.Files.Api.Endpoints;
using static TaskForge.Files.Api.Services.Access.FilesApiAccessService;
using static TaskForge.Files.Api.Services.Common.FilesApiCommonService;
using static TaskForge.Files.Api.Services.Image.FilesApiImageService;
using static TaskForge.Files.Api.Services.Mapping.FilesApiMappingService;
using static TaskForge.Files.Api.Services.Serialization.FilesApiSerializationService;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("files-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "files-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var maxUploadBytes = builder.Configuration.GetValue<long?>("Files:MaxUploadBytes")
    ?? builder.Configuration.GetValue<long?>("Files:MaxImageUploadBytes")
    ?? 10L * 1024 * 1024;
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
    options.ValueLengthLimit = 1024 * 1024;
    options.MultipartHeadersLengthLimit = 64 * 1024;
});
builder.Services.AddDbContext<FilesDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var endpoint = Required(cfg, "S3:Endpoint", "S3__Endpoint");
    var accessKey = Required(cfg, "S3:AccessKey", "S3__AccessKey");
    var secretKey = Required(cfg, "S3:SecretKey", "S3__SecretKey");
    var region = cfg["S3:Region"] ?? cfg["S3__Region"] ?? "us-east-1";
    var forcePathStyle = cfg.GetValue("S3:UsePathStyle", true);
    return new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
    {
        ServiceURL = endpoint,
        ForcePathStyle = forcePathStyle,
        AuthenticationRegion = region,
        UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
    });
});

var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("files-api");
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var s = app.Services.CreateScope();
    var db = s.ServiceProvider.GetRequiredService<FilesDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for FilesDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for FilesDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var s = app.Services.CreateScope();
    await s.ServiceProvider.GetRequiredService<FilesDbContext>().Database.EnsureCreatedAsync();
}
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("files");

app.MapFilesApiEndpoints();

app.Run();
