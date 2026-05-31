using TaskForge.SupportBot;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient("support-api", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(cfg["SupportApi:BaseUrl"] ?? "http://support-api:8080");
});
builder.Services.AddHostedService<Worker>();
var host = builder.Build();
host.Run();
