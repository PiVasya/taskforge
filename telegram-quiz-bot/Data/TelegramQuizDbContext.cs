using Microsoft.EntityFrameworkCore;
using TelegramQuizBot.Data.Entities;

namespace TelegramQuizBot.Data;

public sealed class TelegramQuizDbContext : DbContext
{
    public TelegramQuizDbContext(DbContextOptions<TelegramQuizDbContext> options) : base(options) { }

    public DbSet<AuthorizedTeacher> AuthorizedTeachers => Set<AuthorizedTeacher>();
    public DbSet<WhitelistEntry> Whitelist => Set<WhitelistEntry>();
    public DbSet<StartLogEntry> StartLog => Set<StartLogEntry>();
    public DbSet<ProgressEntry> Progress => Set<ProgressEntry>();
    public DbSet<QuizQuestion> Quizzes => Set<QuizQuestion>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryStat> CategoryStats => Set<CategoryStat>();
    public DbSet<SubcategoryStat> SubcategoryStats => Set<SubcategoryStat>();
    public DbSet<SmartProgressEntry> SmartProgress => Set<SmartProgressEntry>();
    public DbSet<UserSetting> UserSettings => Set<UserSetting>();
    public DbSet<TechnicalBreak> TechnicalBreak => Set<TechnicalBreak>();
    public DbSet<DiagnosticResult> DiagnosticResults => Set<DiagnosticResult>();
    public DbSet<DiagnosticTest> DiagnosticTests => Set<DiagnosticTest>();
    public DbSet<TestQuestion> TestQuestions => Set<TestQuestion>();
    public DbSet<UserAnswer> UserAnswers => Set<UserAnswer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthorizedTeacher>(entity =>
        {
            entity.ToTable("authorized_teachers");
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<WhitelistEntry>(entity =>
        {
            entity.ToTable("whitelist");
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.ExpireTime).HasColumnName("expire_time");
            entity.Property(x => x.LastNotification).HasColumnName("last_notification");
        });

        modelBuilder.Entity<StartLogEntry>(entity =>
        {
            entity.ToTable("start_log");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Username).HasColumnName("username");
            entity.Property(x => x.FullName).HasColumnName("full_name");
            entity.Property(x => x.Timestamp).HasColumnName("timestamp").HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ProgressEntry>(entity =>
        {
            entity.ToTable("progress");
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Total).HasColumnName("total").HasDefaultValue(0);
            entity.Property(x => x.Correct).HasColumnName("correct").HasDefaultValue(0);
            entity.Property(x => x.Incorrect).HasColumnName("incorrect").HasDefaultValue(0);
            entity.Property(x => x.Experience).HasColumnName("experience").HasDefaultValue(0);
        });

        modelBuilder.Entity<QuizQuestion>(entity =>
        {
            entity.ToTable("quizzes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Question).HasColumnName("question").IsRequired();
            entity.Property(x => x.Options).HasColumnName("options");
            entity.Property(x => x.CorrectOptionId).HasColumnName("correct_option_id");
            entity.Property(x => x.Explanation).HasColumnName("explanation");
            entity.Property(x => x.Category).HasColumnName("category").HasDefaultValue("Остальное");
            entity.Property(x => x.Subcategory).HasColumnName("subcategory").HasDefaultValue("Без подкатегории");
            entity.Property(x => x.Type).HasColumnName("type").HasDefaultValue("quiz");
            entity.Property(x => x.Answer).HasColumnName("answer").HasDefaultValue(string.Empty);
            entity.Property(x => x.Image).HasColumnName("image").HasDefaultValue(string.Empty);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.Category, x.Subcategory });
        });

        modelBuilder.Entity<Category>(entity =>
        {
            entity.ToTable("categories");
            entity.HasKey(x => x.Name);
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.OrderIndex).HasColumnName("order_index").HasDefaultValue(0);
        });

        modelBuilder.Entity<CategoryStat>(entity =>
        {
            entity.ToTable("category_stats");
            entity.HasKey(x => new { x.UserId, x.Category, x.Subcategory });
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Category).HasColumnName("category");
            entity.Property(x => x.Subcategory).HasColumnName("subcategory");
            entity.Property(x => x.Correct).HasColumnName("correct").HasDefaultValue(0);
            entity.Property(x => x.Incorrect).HasColumnName("incorrect").HasDefaultValue(0);
        });

        modelBuilder.Entity<SubcategoryStat>(entity =>
        {
            entity.ToTable("subcategory_stats");
            entity.HasKey(x => new { x.UserId, x.Category, x.Subcategory });
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Category).HasColumnName("category");
            entity.Property(x => x.Subcategory).HasColumnName("subcategory");
            entity.Property(x => x.Correct).HasColumnName("correct").HasDefaultValue(0);
            entity.Property(x => x.Incorrect).HasColumnName("incorrect").HasDefaultValue(0);
        });

        modelBuilder.Entity<SmartProgressEntry>(entity =>
        {
            entity.ToTable("smart_progress");
            entity.HasKey(x => new { x.UserId, x.Subcategory });
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Subcategory).HasColumnName("subcategory");
            entity.Property(x => x.Correct).HasColumnName("correct").HasDefaultValue(0);
            entity.Property(x => x.Incorrect).HasColumnName("incorrect").HasDefaultValue(0);
        });

        modelBuilder.Entity<UserSetting>(entity =>
        {
            entity.ToTable("user_settings");
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.LearningMode).HasColumnName("learning_mode").HasDefaultValue("normal");
            entity.Property(x => x.SelectedCategory).HasColumnName("selected_category").HasDefaultValue("all");
            entity.Property(x => x.DiagnosticCompleted).HasColumnName("diagnostic_completed").HasDefaultValue(false);
        });

        modelBuilder.Entity<TechnicalBreak>(entity =>
        {
            entity.ToTable("technical_break");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(false);
            entity.Property(x => x.EndTime).HasColumnName("end_time");
        });

        modelBuilder.Entity<DiagnosticResult>(entity =>
        {
            entity.ToTable("diagnostic_results");
            entity.HasKey(x => new { x.UserId, x.Subcategory });
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Subcategory).HasColumnName("subcategory");
            entity.Property(x => x.Correct).HasColumnName("correct");
        });

        modelBuilder.Entity<DiagnosticTest>(entity =>
        {
            entity.ToTable("diagnostic_tests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Title).HasColumnName("title").IsRequired();
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.QuestionIds).HasColumnName("question_ids").IsRequired();
        });

        modelBuilder.Entity<TestQuestion>(entity =>
        {
            entity.ToTable("test_questions");
            entity.HasKey(x => new { x.TestId, x.QuizId });
            entity.Property(x => x.TestId).HasColumnName("test_id");
            entity.Property(x => x.QuizId).HasColumnName("quiz_id");
        });

        modelBuilder.Entity<UserAnswer>(entity =>
        {
            entity.ToTable("user_answers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Username).HasColumnName("username");
            entity.Property(x => x.QuizId).HasColumnName("quiz_id");
            entity.Property(x => x.Correct).HasColumnName("correct");
            entity.Property(x => x.Timestamp).HasColumnName("timestamp").HasDefaultValueSql("now()");
        });
    }
}
