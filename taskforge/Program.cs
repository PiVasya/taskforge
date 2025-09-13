using TaskForge.Services;
using Microsoft.EntityFrameworkCore;
using TaskForge.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        builder => builder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")
    ));

builder.Services.AddControllers();
builder.Services.AddSingleton<PasswordHasher>();


var app = builder.Build();

app.UseCors("AllowAll");

app.MapControllers();

app.Run();
