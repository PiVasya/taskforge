using TaskForge.Solutions.RatingWorker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("rating-worker");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "rating-worker");
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();
var host = builder.Build();
host.Run();
