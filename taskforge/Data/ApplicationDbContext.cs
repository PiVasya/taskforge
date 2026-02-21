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
        public DbSet<CourseOwner> CourseOwners { get; set; } = null!;
        public DbSet<CourseVisibleGroup> CourseVisibleGroups { get; set; } = null!;

        public DbSet<UserGroup> UserGroups { get; set; } = null!;
        public DbSet<UserGroupMember> UserGroupMembers { get; set; } = null!;
        public DbSet<TaskAssignment> TaskAssignments { get; set; } = null!;
        public DbSet<TaskTestCase> TaskTestCases { get; set; } = null!;
        public DbSet<UserTaskSolution> UserTaskSolutions { get; set; } = null!;
        public DbSet<UserImageTaskSolution> UserImageTaskSolutions { get; set; } = null!;
        public DbSet<UserQuotaBucket> UserQuotaBuckets { get; set; } = null!;
        public DbSet<UserUiSettings> UserUiSettings { get; set; } = null!;
        public DbSet<SupportTicket> SupportTickets { get; set; } = null!;
        public DbSet<SupportMessage> SupportMessages { get; set; } = null!;

        /// <summary>
        /// Records of individual user logins including timestamp, IP address and
        /// user‑agent. Useful for auditing and security analytics.
        /// </summary>
        public DbSet<UserLoginLog> UserLoginLogs { get; set; } = null!;

        // ===== Test (quiz) задания =====
        public DbSet<TaskTestSettings> TaskTestSettings { get; set; } = null!;
        public DbSet<TaskTestQuestion> TaskTestQuestions { get; set; } = null!;
        public DbSet<UserTaskTestAttempt> UserTaskTestAttempts { get; set; } = null!;

        // Наборы данных для бейджей и связей между пользователями и бейджами.
        public DbSet<Badge> Badges { get; set; } = null!;
        public DbSet<UserBadge> UserBadges { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // TaskAssignment code policy JSON columns
            modelBuilder.Entity<TaskAssignment>(entity =>
            {
                entity.Property(x => x.CodeForbiddenCallsJson).HasColumnType("jsonb");
                entity.Property(x => x.CodeRequiredCallsJson).HasColumnType("jsonb");
            });

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

            // 🔹 UserUiSettings (1:1)
            modelBuilder.Entity<UserUiSettings>()
                .HasIndex(x => x.UserId)
                .IsUnique();

            modelBuilder.Entity<UserUiSettings>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserUiSettings>()
                .Property(x => x.UpdatedAtUtc)
                .HasColumnType("timestamp with time zone");

            // 🔹 Course
            modelBuilder.Entity<Course>()
                .Property(c => c.CreatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<Course>()
                .Property(c => c.UpdatedAt)
                .HasColumnType("timestamp with time zone");

            // ===== UserGroups =====
            modelBuilder.Entity<UserGroup>()
                .HasIndex(x => x.Code)
                .IsUnique();

            modelBuilder.Entity<UserGroup>()
                .Property(x => x.TagsJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<UserGroup>()
                .Property(x => x.CreatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserGroup>()
                .Property(x => x.UpdatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserGroupMember>()
                .HasKey(x => new { x.UserId, x.GroupId });

            modelBuilder.Entity<UserGroupMember>()
                .Property(x => x.AddedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserGroupMember>()
                .HasOne(x => x.User)
                .WithMany(u => u.GroupMembers)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserGroupMember>()
                .HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            // ===== Course owners / visible groups =====
            modelBuilder.Entity<CourseOwner>()
                .HasKey(x => new { x.CourseId, x.UserId });

            modelBuilder.Entity<CourseOwner>()
                .Property(x => x.AddedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<CourseOwner>()
                .HasOne(x => x.Course)
                .WithMany(c => c.Owners)
                .HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<CourseOwner>()
                .HasOne(x => x.User)
                .WithMany(u => u.OwnedCourses)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<CourseVisibleGroup>()
                .HasKey(x => new { x.CourseId, x.GroupId });

            modelBuilder.Entity<CourseVisibleGroup>()
                .Property(x => x.AddedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<CourseVisibleGroup>()
                .HasOne(x => x.Course)
                .WithMany(c => c.VisibleGroups)
                .HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<CourseVisibleGroup>()
                .HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

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

            // 🔹 UserImageTaskSolution
            modelBuilder.Entity<UserImageTaskSolution>()
                .HasIndex(x => new { x.UserId, x.TaskAssignmentId, x.CreatedAtUtc });

            modelBuilder.Entity<UserImageTaskSolution>()
                .Property(x => x.CreatedAtUtc)
                .HasColumnType("timestamp with time zone");

            // 🔹 UserQuotaBucket (token bucket quotas)
            // NOTE: some Npgsql EF Core versions don't have UseXminAsConcurrencyToken().
            // Use a shadow property mapped to Postgres system column xmin as an optimistic
            // concurrency token.
            modelBuilder.Entity<UserQuotaBucket>()
                .Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            modelBuilder.Entity<UserQuotaBucket>()
                .HasIndex(x => new { x.UserId, x.BucketType })
                .IsUnique();

            modelBuilder.Entity<UserQuotaBucket>()
                .Property(x => x.CreatedAtUtc)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserQuotaBucket>()
                .Property(x => x.UpdatedAtUtc)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserQuotaBucket>()
                .Property(x => x.LastRefillAtUtc)
                .HasColumnType("timestamp with time zone");


            // 🔹 Badge
            modelBuilder.Entity<Badge>()
                .Property(b => b.CreatedAt)
                .HasColumnType("timestamp with time zone");

            // 🔹 UserBadge
            modelBuilder.Entity<UserBadge>()
                .Property(ub => ub.AwardedAt)
                .HasColumnType("timestamp with time zone");

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

            // 🔹 UserLoginLog
            modelBuilder.Entity<UserLoginLog>()
                .Property(l => l.LoginAt)
                .HasColumnType("timestamp with time zone");

            // 🔹 SupportTicket
            modelBuilder.Entity<SupportTicket>()
                .Property(t => t.CreatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<SupportTicket>()
                .Property(t => t.UpdatedAt)
                .HasColumnType("timestamp with time zone");

            // 🔹 SupportMessage
            modelBuilder.Entity<SupportMessage>()
                .Property(m => m.CreatedAt)
                .HasColumnType("timestamp with time zone");

            // ===== Test (quiz) задания =====

            modelBuilder.Entity<TaskTestSettings>()
                .HasIndex(x => x.TaskAssignmentId)
                .IsUnique();

            modelBuilder.Entity<TaskTestSettings>()
                .Property(x => x.AttemptTimeLimitsJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<TaskTestSettings>()
                .Property(x => x.CreatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<TaskTestSettings>()
                .Property(x => x.UpdatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<TaskTestSettings>()
                .HasOne(x => x.TaskAssignment)
                .WithMany()
                .HasForeignKey(x => x.TaskAssignmentId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<TaskTestQuestion>()
                .HasIndex(x => new { x.TaskAssignmentId, x.Order });

            modelBuilder.Entity<TaskTestQuestion>()
                .Property(x => x.DataJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<TaskTestQuestion>()
                .Property(x => x.CreatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<TaskTestQuestion>()
                .Property(x => x.UpdatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<TaskTestQuestion>()
                .HasOne(x => x.TaskAssignment)
                .WithMany()
                .HasForeignKey(x => x.TaskAssignmentId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserTaskTestAttempt>()
                .HasIndex(x => new { x.TaskAssignmentId, x.UserId, x.AttemptNumber })
                .IsUnique();

            modelBuilder.Entity<UserTaskTestAttempt>()
                .HasIndex(x => new { x.TaskAssignmentId, x.UserId });

            modelBuilder.Entity<UserTaskTestAttempt>()
                .Property(x => x.QuestionOrderJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<UserTaskTestAttempt>()
                .Property(x => x.AnswersJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<UserTaskTestAttempt>()
                .Property(x => x.CreatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserTaskTestAttempt>()
                .Property(x => x.UpdatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserTaskTestAttempt>()
                .Property(x => x.StartedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserTaskTestAttempt>()
                .Property(x => x.SubmittedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserTaskTestAttempt>()
                .HasOne(x => x.TaskAssignment)
                .WithMany()
                .HasForeignKey(x => x.TaskAssignmentId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserTaskTestAttempt>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // ===== UserGroups =====
            modelBuilder.Entity<UserGroup>()
                .HasIndex(x => x.Code)
                .IsUnique();

            modelBuilder.Entity<UserGroup>()
                .Property(x => x.TagsJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<UserGroup>()
                .Property(x => x.CreatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserGroup>()
                .Property(x => x.UpdatedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserGroupMember>()
                .HasKey(x => new { x.UserId, x.GroupId });

            modelBuilder.Entity<UserGroupMember>()
                .Property(x => x.AddedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<UserGroupMember>()
                .HasOne(x => x.User)
                .WithMany(u => u.GroupMembers)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserGroupMember>()
                .HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            // ===== CourseOwners / VisibleGroups =====
            modelBuilder.Entity<CourseOwner>()
                .HasKey(x => new { x.CourseId, x.UserId });

            modelBuilder.Entity<CourseOwner>()
                .Property(x => x.AddedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<CourseOwner>()
                .HasOne(x => x.Course)
                .WithMany(c => c.Owners)
                .HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<CourseOwner>()
                .HasOne(x => x.User)
                .WithMany(u => u.OwnedCourses)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<CourseVisibleGroup>()
                .HasKey(x => new { x.CourseId, x.GroupId });

            modelBuilder.Entity<CourseVisibleGroup>()
                .Property(x => x.AddedAt)
                .HasColumnType("timestamp with time zone");

            modelBuilder.Entity<CourseVisibleGroup>()
                .HasOne(x => x.Course)
                .WithMany(c => c.VisibleGroups)
                .HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<CourseVisibleGroup>()
                .HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}