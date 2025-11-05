using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.ComponentModel.DataAnnotations;
using System.Text;

using taskforge.Data;
using taskforge.Services;
using taskforge.Services.Interfaces;
using taskforge.Services.Remote;

var builder = WebApplication.CreateBuilder(args);

// базовое логирование
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// тек. пользователь
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

// доменные сервисы
builder.Services.AddScoped<ICourseService, CourseService>();
builder.Services.AddScoped<IAssignmentService, AssignmentService>();
builder.Services.AddScoped<ISolutionService, SolutionService>();

// компиляторы/раннеры
builder.Services.AddHttpClient();
builder.Services.AddScoped<ICompilerService, CompilerService>();

// Регистрируем удалённые компиляторы (HTTP-клиенты к раннерам)
builder.Services.AddScoped<ICompiler, CSharpHttpCompiler>();
builder.Services.AddScoped<ICompiler, CppHttpCompiler>();
builder.Services.AddScoped<ICompiler, PythonHttpCompiler>();

builder.Services.AddScoped<ICompilerProvider, CompilerProvider>();
builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<IJudgeService, JudgeService>();
builder.Services.AddScoped<ISolutionAdminService, SolutionAdminService>();
// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// БД
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

builder.Services.AddSingleton<PasswordHasher>();

// JWT
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection.GetValue<string>("Key")
    ?? throw new InvalidOperationException("Jwt:Key is not configured");

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenHandlers.Clear();
        options.TokenHandlers.Add(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler());
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,   // <<< Важно для [Authorize(Roles="Admin")]
            NameClaimType = System.Security.Claims.ClaimTypes.NameIdentifier
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// миграции
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}

// ===== Глобальный маппинг исключений -> корректные HTTP-коды =====
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (UnauthorizedAccessException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
    catch (KeyNotFoundException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
    catch (ValidationException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
    catch (DbUpdateException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(new { message = "Конфликт сохранения данных", detail = ex.Message });
    }
});
// ================================================================

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
