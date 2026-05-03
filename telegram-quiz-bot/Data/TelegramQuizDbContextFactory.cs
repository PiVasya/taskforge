using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TelegramQuizBot.Data;

public sealed class TelegramQuizDbContextFactory : IDesignTimeDbContextFactory<TelegramQuizDbContext>
{
    public TelegramQuizDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__TelegramQuizDb") ??
            Environment.GetEnvironmentVariable("TELEGRAM_QUIZ_MIGRATION_CONNECTION") ??
            "Host=localhost;Port=5432;Database=taskforge_telegram_quiz;Username=taskforge_telegram_quiz;Password=taskforge_telegram_quiz";

        var options = new DbContextOptionsBuilder<TelegramQuizDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new TelegramQuizDbContext(options);
    }
}
