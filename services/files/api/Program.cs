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

// Schema migrations and one-off initialization are deployment jobs in a cluster.
// Do not let every API replica race to mutate the schema during startup.
if (builder.Configuration.GetValue("Cluster:MultiNode", false) &&
    !builder.Configuration.GetValue("Database:MigrationJobOnly", false) &&
    (builder.Configuration.GetValue("Database:MigrateOnStartup", true) ||
     builder.Configuration.GetValue("Database:EnsureCreated", false)))
{
    throw new InvalidOperationException(
        "Multi-node API replicas must not run automatic database migrations or EnsureCreated. " +
        "Set Database__MigrateOnStartup=false and Database__EnsureCreated=false; use a dedicated migration job.");
}

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

// Explicit, opt-in protection for the new K3s environment: never silently send
// its writes (or EF schema migrations) to the legacy PostgreSQL server.
if (builder.Configuration.GetValue("Database:RequireYugabyte", false))
{
    using var ysqlGuardScope = app.Services.CreateScope();
    var ysqlGuardDb = ysqlGuardScope.ServiceProvider.GetRequiredService<FilesDbContext>();
    var ysqlGuardConnection = ysqlGuardDb.Database.GetDbConnection();
    await ysqlGuardConnection.OpenAsync();
    try
    {
        await using var ysqlGuardCommand = ysqlGuardConnection.CreateCommand();
        ysqlGuardCommand.CommandText = "SELECT version()";
        var ysqlVersion = (await ysqlGuardCommand.ExecuteScalarAsync())?.ToString();
        if (ysqlVersion is null || !ysqlVersion.Contains("-YB-", global::System.StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("K3s requires YugabyteDB YSQL, but the connected engine is not YugabyteDB.");
    }
    finally
    {
        await ysqlGuardConnection.CloseAsync();
    }
}

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

// Kubernetes runs EF migrations in a single-run Job using this same API image.
// The Job executes the existing migration block above and exits before binding
// HTTP ports or starting BackgroundService workers.
if (builder.Configuration.GetValue("Database:MigrationJobOnly", false))
{
    if (!builder.Configuration.GetValue("Database:MigrateOnStartup", true) ||
        builder.Configuration.GetValue("Database:EnsureCreated", false))
        throw new InvalidOperationException("MigrationJobOnly requires MigrateOnStartup=true and EnsureCreated=false.");
    app.Logger.LogInformation("EF migration Job completed; exiting without starting API or hosted workers.");
}
else
{
    app.Run();
}
