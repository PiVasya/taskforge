using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using System.Text;

using taskforge.Data;
using taskforge.Services;
using taskforge.Services.Assignments;
using taskforge.Services.Interfaces;
using taskforge.Services.Remote;
using taskforge.Services.Support;
using taskforge.Hubs;
using taskforge.Services.Files;
using Amazon.S3;
using Amazon;

var builder = WebApplication.CreateBuilder(args);

// базовое логирование
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// текущий пользователь и сервис контекста
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

// доступ к курсам / видимость / owners / группы
builder.Services.AddScoped<ICourseAccessService, taskforge.Services.Courses.CourseAccessService>();
builder.Services.AddScoped<IUserGroupService, taskforge.Services.UserGroups.UserGroupService>();

// доменные сервисы
builder.Services.AddScoped<ICourseService, CourseService>();
builder.Services.AddScoped<IAssignmentService, AssignmentService>();
builder.Services.AddScoped<ITaskTestService, TaskTestService>();
builder.Services.AddScoped<ISolutionService, SolutionService>();
builder.Services.AddScoped<IJudgeService, JudgeService>();
builder.Services.AddScoped<ISolutionAdminService, SolutionAdminService>();
builder.Services.AddScoped<ILeaderboardService, LeaderboardService>();

// поддержка
builder.Services.AddScoped<ISupportService, SupportService>();

// регистрация сервиса бейджей
builder.Services.AddScoped<IBadgeService, BadgeService>();

// S3 (MinIO) хранилище для медиа (картинки в условиях, вложения и т.п.)
builder.Services.Configure<S3StorageOptions>(builder.Configuration.GetSection("S3"));
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<S3StorageOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opt.Endpoint) || string.IsNullOrWhiteSpace(opt.AccessKey) || string.IsNullOrWhiteSpace(opt.SecretKey) || string.IsNullOrWhiteSpace(opt.Bucket))
    {
        // В dev окружении можно оставить пустым, но тогда эндпоинты файлов не будут работать.
        // Бросаем исключение только при фактическом вызове в сервисе.
    }

    var cfg = new AmazonS3Config
    {
        ServiceURL = opt.Endpoint,
        ForcePathStyle = opt.UsePathStyle,
        // Region обязателен для некоторых SDK; для MinIO обычно us-east-1
        RegionEndpoint = RegionEndpoint.GetBySystemName(string.IsNullOrWhiteSpace(opt.Region) ? "us-east-1" : opt.Region),
    };

    return new AmazonS3Client(opt.AccessKey, opt.SecretKey, cfg);
});
builder.Services.AddScoped<IFileStorageService, S3FileStorageService>();

// компиляторы/раннеры
builder.Services.AddHttpClient();
builder.Services.AddScoped<ICompilerService, CompilerService>();
builder.Services.AddScoped<ICompiler, CSharpHttpCompiler>();
builder.Services.AddScoped<ICompiler, CppHttpCompiler>();
builder.Services.AddScoped<ICompiler, PythonHttpCompiler>();
builder.Services.AddScoped<ICompiler, JavascriptHttpCompiler>();
builder.Services.AddScoped<ICompiler, PascalHttpCompiler>();
builder.Services.AddScoped<ICompiler, JavaHttpCompiler>();
builder.Services.AddScoped<ICompilerProvider, CompilerProvider>();

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

// Controllers
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// SignalR (уведомления поддержки)
builder.Services.AddSignalR();

// Парольный хэшер как singleton
builder.Services.AddSingleton<PasswordHasher>();

// JWT
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection.GetValue<string>("Key")
    ?? throw new InvalidOperationException("Jwt:Key is not configured");

// Symmetric key bytes for signing/validation
var key = Encoding.UTF8.GetBytes(jwtKey);


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
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(key),
            // Use the standard Role claim type so that [Authorize(Roles = "Admin")] works consistently
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            NameClaimType = System.Security.Claims.ClaimTypes.NameIdentifier
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (!string.IsNullOrEmpty(context.Token))
                    return Task.CompletedTask;

                var path = context.HttpContext.Request.Path;

                var qsToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(qsToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = qsToken;
                    return Task.CompletedTask;
                }

                if (context.Request.Cookies.TryGetValue("tf_at", out var cookieToken) && !string.IsNullOrEmpty(cookieToken))
                {
                    context.Token = cookieToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();


var app = builder.Build();

//
// ✅ Автоприменение миграций при запуске
// (без этого новые таблицы/изменения схемы не появятся и будут 500 ошибки)
//
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger("DatabaseMigration");

    try
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.Migrate();
        logger.LogInformation("Database migrations applied successfully");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to apply database migrations");
        throw; // важно: пусть сервис не стартует в поломанном состоянии
    }
}

// глобальный маппинг исключений -> корректные HTTP-коды
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

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

// отдаём статические файлы из wwwroot (например, изображения бейджей)
app.UseStaticFiles();

app.MapControllers();

// SignalR хаб поддержки
app.MapHub<SupportHub>("/hubs/support");

app.Run();
