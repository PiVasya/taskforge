using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TaskForge.Solutions.Api.Data;

/// <summary>Model tooling must not execute web startup, migrations or dependency connections.</summary>
public sealed class SolutionsDbContextFactory : IDesignTimeDbContextFactory<SolutionsDbContext>
{
    public SolutionsDbContext CreateDbContext(string[] args)
    {
        // Deliberately unreachable default. Schema generation needs no running database.
        var connection = Environment.GetEnvironmentVariable("TASKFORGE_EF_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            connection = "Host=127.0.0.1;Port=1;Database=taskforge_solutions_design;Username=design;Password=unused;Timeout=1";
        var options = new DbContextOptionsBuilder<SolutionsDbContext>().UseNpgsql(connection).Options;
        return new SolutionsDbContext(options);
    }
}
