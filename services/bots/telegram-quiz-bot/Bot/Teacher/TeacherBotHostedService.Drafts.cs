using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramQuizBot.Configuration;
using TelegramQuizBot.Services;
using TelegramQuizBot.Storage;

namespace TelegramQuizBot.Bot;

public sealed partial class TeacherBotHostedService
{
    private static async Task SavePollQuizAsync(ITelegramBotClient bot, Message message, Poll poll, QuizService quizzes, CancellationToken ct)
    {
        TelegramDebugTrace.Write("teacher.quiz", "save-poll:start", ("chatId", message.Chat.Id), ("pollId", poll.Id), ("question", poll.Question), ("type", poll.Type), ("correctOptionId", poll.CorrectOptionId), ("options", string.Join(" | ", poll.Options.Select(x => x.Text))));
        if (!string.Equals(poll.Type, "quiz", StringComparison.OrdinalIgnoreCase) || poll.CorrectOptionId is null)
        {
            TelegramDebugTrace.Write("teacher.quiz", "save-poll:rejected", ("pollId", poll.Id), ("type", poll.Type), ("correctOptionId", poll.CorrectOptionId));
            await bot.SendTextMessageAsync(message.Chat.Id, "❌ Нужен Telegram-опрос типа quiz с правильным ответом.", cancellationToken: ct);
            return;
        }

        var saved = await quizzes.AddPollQuizAsync(
            poll.Question,
            poll.Options.Select(x => x.Text).ToList(),
            poll.CorrectOptionId.Value,
            poll.Explanation,
            CategoryService.DefaultCategory,
            CategoryService.DefaultSubcategory,
            null,
            ct);

        TelegramDebugTrace.Write("teacher.quiz", "save-poll:done", ("quizId", saved.Id), ("category", saved.Category), ("subcategory", saved.Subcategory));
        await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Quiz-вопрос сохранён. ID: {saved.Id}", cancellationToken: ct);
    }

    private async Task ContinueDraftAsync(ITelegramBotClient bot, Message message, QuizService quizzes, TeacherDraftQuestion draft, CancellationToken ct)
    {
        var teacherId = message.From!.Id;
        var text = message.Text?.Trim() ?? string.Empty;

        switch (draft.Step)
        {
            case "image":
                if (text == "/skip")
                {
                    draft.Step = "question";
                    await bot.SendTextMessageAsync(message.Chat.Id, "📝 Введите текст вопроса:", cancellationToken: ct);
                }
                break;
            case "question":
                draft.Question = text;
                draft.Step = "answer";
                await bot.SendTextMessageAsync(message.Chat.Id, "✅ Теперь введите правильный ответ:", cancellationToken: ct);
                break;
            case "answer":
                draft.Answer = text;
                draft.Step = "explanation";
                await bot.SendTextMessageAsync(message.Chat.Id, "💬 Введите пояснение или '-' если пояснение не нужно:", cancellationToken: ct);
                break;
            case "explanation":
                draft.Explanation = text == "-" ? string.Empty : text;
                draft.Step = "category";
                await bot.SendTextMessageAsync(message.Chat.Id, "📁 Введите категорию или '-' для 'Остальное':", cancellationToken: ct);
                break;
            case "category":
                draft.Category = text == "-" ? CategoryService.DefaultCategory : text;
                draft.Step = "subcategory";
                await bot.SendTextMessageAsync(message.Chat.Id, "📂 Введите подкатегорию или '-' для 'Без подкатегории':", cancellationToken: ct);
                break;
            case "subcategory":
                draft.Subcategory = text == "-" ? CategoryService.DefaultSubcategory : text;
                var saved = await quizzes.AddTextQuestionAsync(draft.Question!, draft.Answer!, draft.Explanation, draft.Category, draft.Subcategory, draft.ImageKey, ct);
                _state.Drafts.TryRemove(teacherId, out _);
                await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Текстовый вопрос сохранён. ID: {saved.Id}", cancellationToken: ct);
                break;
        }
    }

}
