using Runner.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<ICompilationService, RoslynCompilationService>();
builder.Services.AddSingleton<IExecutionService, ExecutionService>();

var app = builder.Build();
app.MapControllers();
app.Run();
