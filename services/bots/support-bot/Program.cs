using TaskForge.SupportBot;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("support-bot");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "support-bot");
builder.Services.AddHttpClient("support-api", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri((cfg["SupportApi:BaseUrl"] ?? cfg["Services:SupportApi"] ?? "http://support-api:8080").TrimEnd('/') + "/");
});
builder.Services.AddHttpClient("identity-api", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri((cfg["IdentityApi:BaseUrl"] ?? cfg["Services:IdentityApi"] ?? "http://identity-api:8080").TrimEnd('/') + "/");
});
builder.Services.AddHostedService<Worker>();
var host = builder.Build();
host.Run();
