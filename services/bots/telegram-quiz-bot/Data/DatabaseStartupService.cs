using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TelegramQuizBot.Configuration;
using TelegramQuizBot.Data.Entities;

namespace TelegramQuizBot.Data;

public sealed class DatabaseStartupService : IHostedService
{
    private readonly IServiceProvider _provider;
    private readonly ILogger<DatabaseStartupService> _logger;
    private readonly TelegramQuizOptions _options;

    public DatabaseStartupService(IServiceProvider provider, ILogger<DatabaseStartupService> logger, IOptions<TelegramQuizOptions> options)
    {
        _provider = provider;
        _logger = logger;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TelegramQuizDbContext>();

        if (_options.ApplyMigrationsOnStartup)
        {
            _logger.LogInformation("Applying TelegramQuiz EF Core migrations");
            await db.Database.MigrateAsync(cancellationToken);
        }

        var exists = await db.TechnicalBreak.AnyAsync(x => x.Id == 1, cancellationToken);
        if (!exists)
        {
            db.TechnicalBreak.Add(new TechnicalBreak { Id = 1, IsActive = false });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
