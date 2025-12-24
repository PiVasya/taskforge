using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;

namespace SupportBot
{
    /// <summary>
    /// Точка входа для контейнера поддержки. Настраивает DI и запускает hosted service.
    /// </summary>
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    // База данных
                    services.AddDbContext<ApplicationDbContext>(options =>
                        options.UseNpgsql(context.Configuration.GetConnectionString("DefaultConnection")));
                    // HttpClient для вызовов в API
                    services.AddHttpClient();
                    // Наш hosted service
                    services.AddHostedService<SupportBotService>();
                })
                .Build();

            await host.RunAsync();
        }
    }
}