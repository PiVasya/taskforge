using Microsoft.EntityFrameworkCore;

namespace TaskForge.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        // Здесь определяйте DbSet<TEntity> для сущностей, например:
        // public DbSet<TaskItem> Tasks { get; set; }
    }
}
