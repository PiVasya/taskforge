using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Logging;
using System.Text;
using taskforge.Services.Interfaces;
using TaskForge.Data;
using TaskForge.Services;

var builder = WebApplication.CreateBuilder(args);

// Включаем консольное логирование и задаём минимальный уровень
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Debug);

builder.Services.AddScoped<ICompilerService, CompilerService>();

// Конфигурация CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// Контекст базы данных
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Контроллеры и сервис хэширования паролей
builder.Services.AddControllers();
builder.Services.AddSingleton<PasswordHasher>();

// Получаем секцию Jwt и проверяем наличие ключа
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection.GetValue<string>("Key");
if (string.IsNullOrWhiteSpace(jwtKey))
{
    throw new InvalidOperationException("Jwt:Key is not configured");
}

// Настройка аутентификации (JWT)
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSection["Issuer"],
        ValidAudience = jwtSection["Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };
});

builder.Services.AddAuthorization();

var app = builder.Build();

app.Logger.LogInformation("Application starting...");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    app.Logger.LogInformation("Applying pending migrations...");
    db.Database.Migrate();
}

// Подключаем CORS, аутентификацию и авторизацию
app.UseCors("AllowAll");

app.Logger.LogInformation("Configuring authentication and authorization middleware");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Logger.LogInformation("Ready to accept requests");
app.Run();
