using TaskForge.Execution.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();
var host = builder.Build();
host.Run();
