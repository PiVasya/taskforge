using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using System.Text;
using System.Net.Http;

using taskforge.Data;
using taskforge.Services;
using taskforge.Services.Assignments;
using taskforge.Services.Interfaces;
using taskforge.Services.Remote;
using taskforge.Services.Support;
using taskforge.Hubs;
using taskforge.Services.Files;
using taskforge.Services.ImageTests;
using taskforge.Services.Quotas;
using Amazon.S3;
using Amazon;
using taskforge.Middleware;
using taskforge.Services.AI;

var builder = WebApplication.CreateBuilder(args);

// базовое логирование
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// текущий пользователь и сервис контекста
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IFeatureRoleService, FeatureRoleService>();
builder.Services.AddScoped<IMinecraftChatService, MinecraftChatService>();
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("AI"));

// квоты (token bucket)
builder.Services.AddScoped<IQuotaService, QuotaService>();

// доступ к курсам / видимость / owners / группы
builder.Services.AddScoped<ICourseAccessService, taskforge.Services.Courses.CourseAccessService>();
builder.Services.AddScoped<IUserGroupService, taskforge.Services.UserGroups.UserGroupService>();

// доменные сервисы
builder.Services.AddScoped<ICourseService, CourseService>();
builder.Services.AddScoped<IAssignmentService, AssignmentService>();
builder.Services.AddScoped<ITaskTestService, TaskTestService>();
builder.Services.AddScoped<ITaskMathService, TaskMathService>();
builder.Services.AddScoped<IAiJobService, AiJobService>();
builder.Services.AddScoped<AiChatService>();
builder.Services.AddScoped<AiBootstrapService>();
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

        // ВАЖНО: для MinIO/LocalStack/любого кастомного S3 endpoint
        AuthenticationRegion = opt.Region,
    };


    return new AmazonS3Client(opt.AccessKey, opt.SecretKey, cfg);
});
builder.Services.AddScoped<IFileStorageService, S3FileStorageService>();

// code-analyzer (pre-run forbidden constructs check)
builder.Services.Configure<taskforge.Services.CodeAnalysis.CodeAnalyzerOptions>(builder.Configuration.GetSection("CodeAnalyzer"));
builder.Services.AddHttpClient<taskforge.Services.CodeAnalysis.ICodeAnalyzerClient, taskforge.Services.CodeAnalysis.HttpCodeAnalyzerClient>((sp, c) =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<taskforge.Services.CodeAnalysis.CodeAnalyzerOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(opt.Url))
    {
        var url = opt.Url.TrimEnd('/') + "/";
        c.BaseAddress = new Uri(url);
    }

    var timeoutSeconds = opt.TimeoutSeconds <= 0 ? 6 : opt.TimeoutSeconds;
    c.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});

// image similarity (external analyzer + fallback)
builder.Services.Configure<ImageAnalyzerOptions>(builder.Configuration.GetSection("ImageAnalyzer"));
builder.Services.AddHttpClient<IImageAnalyzerClient, HttpImageAnalyzerClient>((sp, c) =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ImageAnalyzerOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(opt.Url))
    {
        var url = opt.Url.TrimEnd('/') + "/";
        c.BaseAddress = new Uri(url);
    }

    var timeoutSeconds = opt.TimeoutSeconds <= 0 ? 25 : opt.TimeoutSeconds;
    c.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});

builder.Services.AddScoped<IImageSimilarityService, ImageSimilarityService>();

// image-runners (рендер изображений из кода)
builder.Services.Configure<taskforge.Services.ImageRunners.ImageRunnersOptions>(builder.Configuration.GetSection("ImageRunners"));
builder.Services.AddHttpClient<taskforge.Services.ImageRunners.IImageRunnerClient, taskforge.Services.ImageRunners.HttpImageRunnerClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});

// компиляторы/раннеры
builder.Services.AddHttpClient();

// Integrations (Minecraft)
builder.Services.AddHttpClient("minecraft-webhook")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        MaxConnectionsPerServer = 20,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    })
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));

builder.Services.AddScoped<taskforge.Services.Integrations.IMinecraftServerNotifier, taskforge.Services.Integrations.HttpMinecraftServerNotifier>();

builder.Services.AddScoped<ICompilerService, CompilerService>();
builder.Services.AddScoped<ICompiler, CSharpHttpCompiler>();
builder.Services.AddScoped<ICompiler, CppHttpCompiler>();
builder.Services.AddScoped<ICompiler, PythonHttpCompiler>();
builder.Services.AddScoped<ICompiler, JavascriptHttpCompiler>();
builder.Services.AddScoped<ICompiler, PascalHttpCompiler>();
builder.Services.AddScoped<ICompiler, JavaHttpCompiler>();
builder.Services.AddScoped<ICompilerProvider, CompilerProvider>();


object BuildErrorPayload(string message, string path, string traceId, string? detail = null, object? errors = null, string? code = null, string? userHint = null, string severity = "error", params string[] howToFix)
{
    var cleanSteps = howToFix?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? Array.Empty<string>();
    return new
    {
        message,
        detail,
        errors,
        path,
        trace = traceId,
        traceId,
        code,
        userHint,
        howToFix = cleanSteps,
        severity,
    };
}

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
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));

    // Не валим приложение на старте из-за PendingModelChangesWarning.
    // В проекте миграции применяются автоматически при запуске, а часть миграций поддерживается вручную.
    // Если модель слегка расходится со snapshot, это предупреждение не должно ронять сервис раньше самой миграции.
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// Controllers
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(x => x.Value?.Errors.Count > 0)
            .ToDictionary(
                x => string.IsNullOrWhiteSpace(x.Key) ? "form" : x.Key,
                x => x.Value!.Errors
                    .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Некорректное значение" : e.ErrorMessage)
                    .ToArray());

        return new BadRequestObjectResult(BuildErrorPayload(
            message: "Проверьте заполнение формы",
            path: context.HttpContext.Request.Path.ToString(),
            traceId: context.HttpContext.TraceIdentifier,
            errors: errors,
            code: "VALIDATION_FAILED",
            userHint: "Часть полей заполнена некорректно или пропущена.",
            severity: "validation",
            howToFix: new[]
            {
                "Исправьте поля, отмеченные в форме.",
                "Проверьте обязательные значения и повторите сохранение.",
            }
        ));
    };
});

// SignalR (уведомления поддержки)
builder.Services.AddSignalR();

// Парольный хэшер как singleton
builder.Services.AddSingleton<PasswordHasher>();

// new judge pipeline (v2)
builder.Services.AddScoped<taskforge.Services.Interfaces.IPolicyJudgeService, taskforge.Services.PolicyJudgeService>();

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
// ВАЖНО: в проде (и когда миграции гоняются вручную) это часто мешает.
// Поэтому по умолчанию:
//   - Development: применяем миграции автоматически
//   - Production: НЕ применяем (включается env DB_AUTO_MIGRATE=true)
//
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger("DatabaseMigration");

    // По умолчанию миграции применяем автоматически даже в production,
    // потому что БД может быть доступна только изнутри окружения приложения.
    // Отключение доступно через DB_AUTO_MIGRATE=false.
    var autoMigrate = true;
    var envAuto = Environment.GetEnvironmentVariable("DB_AUTO_MIGRATE");
    if (!string.IsNullOrWhiteSpace(envAuto) && bool.TryParse(envAuto, out var parsed))
        autoMigrate = parsed;

    try
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!autoMigrate)
        {
            logger.LogWarning("DB_AUTO_MIGRATE=false -> skipping Database.Migrate() and feature role bootstrap.");
        }
        else
        {
            db.Database.Migrate();
            logger.LogInformation("Database migrations applied successfully");

            var featureRoles = scope.ServiceProvider.GetRequiredService<IFeatureRoleService>();
            await featureRoles.EnsureDefaultRolesAsync();
            await featureRoles.SyncMinecraftLinkedUsersAsync();
            var aiBootstrap = scope.ServiceProvider.GetRequiredService<AiBootstrapService>();
            await aiBootstrap.EnsureSystemUserAsync();
            logger.LogInformation("Feature roles ensured, Minecraft-linked users synced and AI bootstrap finished");
        }
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
        await ctx.Response.WriteAsJsonAsync(BuildErrorPayload(
            message: ex.Message,
            path: ctx.Request.Path.ToString(),
            traceId: ctx.TraceIdentifier,
            code: "FORBIDDEN",
            userHint: "У вас нет доступа к этому действию.",
            severity: "warning",
            howToFix: new[] { "Проверьте свою роль и права доступа.", "Если доступ должен быть, обратитесь к администратору." }
        ));
    }
    catch (KeyNotFoundException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(BuildErrorPayload(
            message: ex.Message,
            path: ctx.Request.Path.ToString(),
            traceId: ctx.TraceIdentifier,
            code: "NOT_FOUND",
            userHint: "Запрошенный объект не найден или уже был удалён.",
            severity: "warning",
            howToFix: new[] { "Обновите страницу и откройте раздел заново.", "Если вы перешли по старой ссылке, вернитесь назад и выберите объект снова." }
        ));
    }
    catch (ValidationException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(BuildErrorPayload(
            message: ex.Message,
            path: ctx.Request.Path.ToString(),
            traceId: ctx.TraceIdentifier,
            code: "VALIDATION_FAILED",
            userHint: "Сервер отклонил запрос из-за некорректных данных.",
            severity: "validation",
            howToFix: new[] { "Проверьте заполненные поля.", "Исправьте данные и повторите действие." }
        ));
    }
    catch (DbUpdateException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(BuildErrorPayload(
            message: "Конфликт сохранения данных",
            detail: ex.InnerException?.Message ?? ex.Message,
            path: ctx.Request.Path.ToString(),
            traceId: ctx.TraceIdentifier,
            code: "SAVE_CONFLICT",
            userHint: "Данные не удалось сохранить, потому что сервер обнаружил конфликт или ограничение базы данных.",
            severity: "warning",
            howToFix: new[] { "Проверьте, не занято ли значение другим объектом.", "Обновите страницу и повторите действие." }
        ));
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("matched multiple endpoints", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(BuildErrorPayload(
            message: "Конфликт маршрутов на сервере",
            detail: ex.Message,
            path: ctx.Request.Path.ToString(),
            traceId: ctx.TraceIdentifier,
            code: "SERVER_ROUTE_CONFLICT",
            userHint: "На сервере столкнулись два маршрута API. Это проблема на стороне приложения, а не ваших данных.",
            severity: "error",
            howToFix: new[] { "Попробуйте повторить действие позже.", "Если ошибка повторяется, передайте администратору Trace ID." }
        ));
    }
    catch (Exception ex)
    {
        // ВАЖНО: иначе редкие нативные падения (OpenCV / ImageMagick и т.п.) уходят в "Unhandled" и рвут запрос.
        // Логи делаем максимально подробными, чтобы потом можно было воспроизвести.
        var trace = ctx.TraceIdentifier;
        var path = ctx.Request.Path.ToString();
        var message = string.IsNullOrWhiteSpace(ex.Message) ? "Внутренняя ошибка" : ex.Message;
        var detail = ex.InnerException?.Message;

        Console.WriteLine($"[Unhandled] trace={trace} path={path} {ex.GetType().Name}: {ex.Message}\n{ex}");
        ctx.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Unhandled")
            .LogError(ex, "Unhandled exception trace={Trace} path={Path}", trace, path);

        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(BuildErrorPayload(
            message: message,
            detail: detail,
            path: path,
            traceId: trace,
            code: "UNHANDLED_SERVER_ERROR",
            userHint: "Это внутренняя ошибка сервера. Ваше действие не завершилось полностью.",
            severity: "error",
            howToFix: new[] { "Попробуйте повторить действие через несколько секунд.", "Если ошибка повторится, сообщите в поддержку и укажите Trace ID." }
        ));
    }
});

app.UseMiddleware<RequestLoggingMiddleware>();

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

// отдаём статические файлы из wwwroot (например, изображения бейджей)
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "taskforge-api" })).AllowAnonymous();
app.MapControllers();

// SignalR хаб поддержки
app.MapHub<SupportHub>("/hubs/support");
app.MapHub<MinecraftChatHub>("/hubs/minecraft-chat");

app.Run();
