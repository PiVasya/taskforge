using TaskForge.Solutions.RatingWorker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("rating-worker");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "rating-worker");
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();
var host = builder.Build();
// Refuse old PostgreSQL in the K3s rating worker before starting background jobs.
if (builder.Configuration.GetValue("Database:RequireYugabyte", false))
{
    await using var ysqlConnection = new Npgsql.NpgsqlConnection(builder.Configuration.GetConnectionString("DefaultConnection"));
    await ysqlConnection.OpenAsync();
    await using var ysqlCheck = ysqlConnection.CreateCommand();
    ysqlCheck.CommandText = "SELECT version()";
    var ysqlVersion = (await ysqlCheck.ExecuteScalarAsync())?.ToString();
    if (ysqlVersion is null || !ysqlVersion.Contains("-YB-", global::System.StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("K3s rating worker requires YugabyteDB YSQL.");
}
host.Run();
