using Amazon.S3;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;
using TaskForge.Observability.Api.Services.Cluster;

using TaskForge.Observability.Api.Endpoints;
using static TaskForge.Observability.Api.Services.Common.ObservabilityApiCommonService;
using static TaskForge.Observability.Api.Services.Mapping.ObservabilityApiMappingService;
using static TaskForge.Observability.Api.Services.Serialization.ObservabilityApiSerializationService;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("observability-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "observability-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var endpoint = cfg["S3:Endpoint"] ?? cfg["S3__Endpoint"] ?? throw new InvalidOperationException("S3 endpoint is required.");
    var accessKey = cfg["S3:AccessKey"] ?? cfg["S3__AccessKey"] ?? throw new InvalidOperationException("S3 access key is required.");
    var secretKey = cfg["S3:SecretKey"] ?? cfg["S3__SecretKey"] ?? throw new InvalidOperationException("S3 secret key is required.");
    var region = cfg["S3:Region"] ?? cfg["S3__Region"] ?? "us-east-1";
    return new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
    {
        ServiceURL = endpoint,
        ForcePathStyle = cfg.GetValue("S3:UsePathStyle", true),
        AuthenticationRegion = region,
        UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
    });
});
builder.Services.AddSingleton<ClusterDiagnosticsArchiveStore>();
builder.Services.AddSingleton<ClusterTelemetryService>();
builder.Services.AddHostedService<ClusterTelemetryService>(sp => sp.GetRequiredService<ClusterTelemetryService>());
builder.Services.AddDbContext<ObservabilityDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("observability-api");
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true)) { using var s = app.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<ObservabilityDbContext>(); app.Logger.LogInformation("Applying EF Core migrations for ObservabilityDbContext..."); await db.Database.MigrateAsync(); app.Logger.LogInformation("EF Core migrations for ObservabilityDbContext applied."); }
else if (builder.Configuration.GetValue("Database:EnsureCreated", false)) { using var s = app.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<ObservabilityDbContext>().Database.EnsureCreatedAsync(); }
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("observability");

app.MapObservabilityApiEndpoints();

app.Run();
