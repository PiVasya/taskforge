using Microsoft.EntityFrameworkCore;
using taskforge.Data.Models;
using taskforge.Data.Models.Entities;

namespace taskforge.Data
{
    /// <summary>
    /// Основной контекст базы данных приложения. Дополнен поддержкой бейджей.
    /// </summary>
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        public DbSet<User> Users { get; set; } = null!;
        public DbSet<Course> Courses { get; set; } = null!;
        public DbSet<TaskAssignment> TaskAssignments { get; set; } = null!;
        public DbSet<TaskTestCase> TaskTestCases { get; set; } = null!;
        public DbSet<UserTaskSolution> UserTaskSolutions { get; set; } = null!;

        // Наборы данных для бейджей и связей между пользователями и бейджами.
        public DbSet<Badge> Badges { get; set; } = null!;
        public DbSet<UserBadge> UserBadges { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 🔹 User
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Email).IsUnique();
            modelBuilder.Entity<User>()
                .Property(u => u.AdditionalDataJson)
                .HasColumnType("jsonb");
            modelBuilder.Entity<User>()
                .Property(u => u.DateOfBirth)
                .HasColumnType("date");
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

            // 🔹 Course
            modelBuilder.Entity<Course>()
                .Property(c => c.CreatedAt)
                .HasColumnType("timestamp with time zone");
            modelBuilder.Entity<Course>()
                .Property(c => c.UpdatedAt)
                .HasColumnType("timestamp with time zone");

            // 🔹 TaskAssignment
            modelBuilder.Entity<TaskAssignment>()
                .Property(t => t.CreatedAt)
                .HasColumnType("timestamp with time zone");
            modelBuilder.Entity<TaskAssignment>()
                .Property(t => t.UpdatedAt)
                .HasColumnType("timestamp with time zone");

            // 🔹 UserTaskSolution
            modelBuilder.Entity<UserTaskSolution>()
                .Property(s => s.SubmittedAt)
                .HasColumnType("timestamp with time zone");

            // 🔹 Badge
            modelBuilder.Entity<Badge>()
                .Property(b => b.CreatedAt)
                .HasColumnType("timestamp with time zone");

            // 🔹 UserBadge
            modelBuilder.Entity<UserBadge>()
                .Property(ub => ub.AwardedAt)
                .HasColumnType("timestamp with time zone");
            // Явно указываем навигационные свойства, чтобы EF использовал существующие
            // ключи UserId и BadgeId в качестве внешних ключей, иначе создаются
            // дублирующие столбцы UserId1/BadgeId1.
            modelBuilder.Entity<UserBadge>()
                .HasOne(ub => ub.User)
                .WithMany()
                .HasForeignKey(ub => ub.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<UserBadge>()
                .HasOne(ub => ub.Badge)
                .WithMany()
                .HasForeignKey(ub => ub.BadgeId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}