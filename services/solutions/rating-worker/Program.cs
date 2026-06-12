using TaskForge.Solutions.RatingWorker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("rating-worker");
builder.Services.AddHostedService<Worker>();
var host = builder.Build();
host.Run();
