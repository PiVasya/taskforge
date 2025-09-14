using Microsoft.EntityFrameworkCore;
using TaskForge.Data.Models;

namespace TaskForge.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        // DbSet для сущности User – EF Core создаст таблицу Users.
        public DbSet<User> Users { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>()
                        .HasIndex(u => u.Email)
                        .IsUnique();

            modelBuilder.Entity<User>()
                        .Property(u => u.AdditionalDataJson)
                        .HasColumnType("jsonb");

            // Дата рождения — тип date
            modelBuilder.Entity<User>()
                        .Property(u => u.DateOfBirth)
                        .HasColumnType("date");

            // UTC‑метки времени — тип timestamptz
            modelBuilder.Entity<User>()
                        .Property(u => u.CreatedAt)
                        .HasColumnType("timestamp with time zone");
            modelBuilder.Entity<User>()
                        .Property(u => u.UpdatedAt)
                        .HasColumnType("timestamp with time zone");
            modelBuilder.Entity<User>()
                        .Property(u => u.LastLoginAt)
                        .HasColumnType("timestamp with time zone");
            modelBuilder.Entity<User>()
                        .Property(u => u.ResetPasswordExpiration)
                        .HasColumnType("timestamp with time zone");
            modelBuilder.Entity<User>()
                        .Property(u => u.LockoutEnd)
                        .HasColumnType("timestamp with time zone");
        }
    }
}
