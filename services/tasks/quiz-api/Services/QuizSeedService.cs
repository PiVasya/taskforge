using Microsoft.EntityFrameworkCore;
using QuizTaskService.Data;
using QuizTaskService.Data.Entities;
using static QuizTaskService.Services.Access.QuizTaskAccessService;
using static QuizTaskService.Services.Common.QuizTaskCommonService;
using static QuizTaskService.Services.Mapping.QuizTaskMappingService;
using static QuizTaskService.Services.Serialization.QuizTaskSerializationService;

namespace QuizTaskService.Services;

public static class QuizSeedService
{
    public static async Task SeedA1SamplesAsync(QuizDbContext db, CancellationToken ct = default)
    {
        if (await db.Tasks.AnyAsync(ct)) return;

        await AddTaskAsync(
            db,
            slug: "a1-vowel-root-polozhit-veschi",
            title: "Пол__жить вещи",
            prompt: "Пол__жить вещи",
            correct: "о",
            answerText: "Положить",
            explanation: "Корень лаг/лож: перед ж пишется о.",
            ct);

        await AddTaskAsync(
            db,
            slug: "a1-vowel-root-polagatsya-na-druga",
            title: "Пол__гаться на друга",
            prompt: "Пол__гаться на друга",
            correct: "а",
            answerText: "Полагаться",
            explanation: "Корень лаг/лож: перед г пишется а.",
            ct);

        await AddTaskAsync(
            db,
            slug: "a1-vowel-root-goret-na-solntse",
            title: "Г__реть на солнце",
            prompt: "Г__реть на солнце",
            correct: "о",
            answerText: "Гореть",
            explanation: "Корень гар/гор: без ударения обычно пишется о.",
            ct);
    }

    private static async Task AddTaskAsync(QuizDbContext db, string slug, string title, string prompt, string correct, string answerText, string explanation, CancellationToken ct)
    {
        var task = new QuizTask
        {
            Slug = slug,
            Type = "vowel-choice",
            Title = title,
            Prompt = prompt,
            SubjectCode = "russian",
            ExamCode = "ct-ce-2026",
            SectionCode = "A1",
            Difficulty = 1,
            TagsJson = "[\"A1\",\"орфография\",\"гласная-в-корне\"]",
            SourceName = "seed",
            IsPublished = true,
            CurrentVersion = 1
        };

        db.Tasks.Add(task);
        db.TaskVersions.Add(new QuizTaskVersion
        {
            TaskId = task.Id,
            VersionNumber = 1,
            DataJson = $"{{\"options\":[\"а\",\"о\"],\"answerText\":\"{answerText}\"}}",
            CorrectAnswerJson = $"{{\"selected\":[\"{correct}\"]}}",
            ExplanationJson = $"{{\"text\":\"{explanation}\"}}",
            ChangeComment = "Initial seed"
        });

        await db.SaveChangesAsync(ct);
    }
}
