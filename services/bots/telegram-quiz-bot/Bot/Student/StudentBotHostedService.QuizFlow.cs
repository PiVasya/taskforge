using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramQuizBot.Configuration;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;
using TelegramQuizBot.Services;

namespace TelegramQuizBot.Bot;

public sealed partial class StudentBotHostedService
{
    private async Task SendNextQuizAsync(ITelegramBotClient bot, long chatId, long userId, QuizService quizzes, TelegramQuizDbContext db, CancellationToken ct)
    {
        var settings = await EnsureSettingsAsync(db, userId, ct);
        TelegramDebugTrace.Write(
            "student.quiz",
            "next:start",
            ("chatId", chatId),
            ("userId", userId),
            ("mode", settings.LearningMode),
            ("selectedCategory", settings.SelectedCategory));
        string? preferredSubcategory = null;

        if (settings.LearningMode == ModeSmart)
        {
            var weakSubcategories = await quizzes.GetWeakSubcategoriesAsync(userId, ct);
            preferredSubcategory = weakSubcategories.FirstOrDefault();
            TelegramDebugTrace.Write("student.quiz", "smart:weak-subcategories", ("userId", userId), ("preferred", preferredSubcategory), ("all", string.Join(",", weakSubcategories)));
        }

        var next = await quizzes.GetNextForUserAsync(userId, settings.SelectedCategory, preferredSubcategory, ct);
        TelegramDebugTrace.Write(
            "student.quiz",
            "next:result",
            ("userId", userId),
            ("quizId", next.Quiz?.Id),
            ("totalAvailable", next.TotalAvailable),
            ("unseenAvailable", next.UnseenAvailable),
            ("repeatCycle", next.IsRepeatCycle),
            ("filterRelaxed", next.FilterWasRelaxed),
            ("preferredSubcategory", preferredSubcategory));
        var quiz = next.Quiz;
        if (quiz == null)
        {
            await bot.SendTextMessageAsync(
                chatId,
                "❌ Вопросов пока нет. База квизов пуста.",
                replyMarkup: StudentHomeKeyboard(),
                cancellationToken: ct);
            return;
        }

        if (next.FilterWasRelaxed)
        {
            await bot.SendTextMessageAsync(chatId, "ℹ️ В выбранной категории или теме вопросов не осталось, поэтому показываю задание из более широкого набора.", cancellationToken: ct);
        }
        else if (next.IsRepeatCycle)
        {
            await bot.SendTextMessageAsync(chatId, "🔄 Вы уже прошли все доступные вопросы по текущей категории. Начинаю новый круг.", cancellationToken: ct);
        }

        if (quiz.Type == "text")
        {
            TelegramDebugTrace.Write("student.quiz", "send:text", ("chatId", chatId), ("userId", userId), ("quizId", quiz.Id), ("category", quiz.Category), ("subcategory", quiz.Subcategory), ("question", quiz.Question));
            _state.PendingTextAnswers[userId] = quiz;
            await bot.SendTextMessageAsync(
                chatId,
                BuildQuizTopicHeader(quiz, settings, next, preferredSubcategory),
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            await bot.SendTextMessageAsync(
                chatId,
                $"📝 <b>{Html(quiz.Question)}</b>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        var options = quiz.GetOptions();
        TelegramDebugTrace.Write("student.quiz", "prepare:poll", ("chatId", chatId), ("userId", userId), ("quizId", quiz.Id), ("category", quiz.Category), ("subcategory", quiz.Subcategory), ("question", quiz.Question), ("options", string.Join(" | ", options)), ("correctOptionId", quiz.CorrectOptionId));
        if (options.Length == 0 || quiz.CorrectOptionId is null)
        {
            await bot.SendTextMessageAsync(chatId, $"⚠️ Вопрос {quiz.Id} повреждён: нет вариантов ответа.", replyMarkup: AfterAnswerKeyboard(false), cancellationToken: ct);
            return;
        }

        await bot.SendTextMessageAsync(
            chatId,
            BuildQuizTopicHeader(quiz, settings, next, preferredSubcategory),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        var sent = await bot.SendPollAsync(
            chatId: chatId,
            question: quiz.Question,
            options: options,
            type: PollType.Quiz,
            correctOptionId: quiz.CorrectOptionId,
            explanation: quiz.Explanation,
            isAnonymous: false,
            cancellationToken: ct);

        if (sent.Poll?.Id is { } pollId)
        {
            _state.ActivePolls[pollId] = new ActivePollQuiz { UserId = userId, ChatId = chatId, Quiz = quiz };
            TelegramDebugTrace.Write("student.quiz", "send:poll:registered", ("chatId", chatId), ("userId", userId), ("quizId", quiz.Id), ("pollId", pollId), ("messageId", sent.MessageId));
        }
        else
        {
            TelegramDebugTrace.Write("student.quiz", "send:poll:no-poll-id", ("chatId", chatId), ("userId", userId), ("quizId", quiz.Id), ("messageId", sent.MessageId));
        }
    }

    private async Task HandlePollAnswerAsync(PollAnswer answer, CancellationToken ct)
    {
        TelegramDebugTrace.Write("student.poll", "answer:received", ("pollId", answer.PollId), ("userId", answer.User.Id), ("username", answer.User.Username), ("optionIds", string.Join(",", answer.OptionIds)));
        if (!_state.ActivePolls.TryRemove(answer.PollId, out var active))
        {
            TelegramDebugTrace.Write("student.poll", "answer:ignored:unknown-poll", ("pollId", answer.PollId), ("userId", answer.User.Id));
            return;
        }

        using var scope = _provider.CreateScope();
        var progress = scope.ServiceProvider.GetRequiredService<ProgressService>();
        var selected = answer.OptionIds.FirstOrDefault();
        var correct = active.Quiz.CorrectOptionId == selected;
        TelegramDebugTrace.Write("student.poll", "answer:checked", ("pollId", answer.PollId), ("userId", answer.User.Id), ("quizId", active.Quiz.Id), ("selected", selected), ("correctOptionId", active.Quiz.CorrectOptionId), ("correct", correct));
        await progress.SaveAnswerAsync(answer.User.Id, answer.User.Username, active.Quiz, correct, ct);

        if (_bot == null)
        {
            TelegramDebugTrace.Write("student.poll", "answer:no-bot-client", ("pollId", answer.PollId), ("quizId", active.Quiz.Id));
            return;
        }

        var text = correct
            ? "🎉 Правильно! Молодец!"
            : "❌ Неправильно, но ты справился! ✨";

        await _bot.SendTextMessageAsync(
            active.ChatId,
            text,
            replyMarkup: AfterAnswerKeyboard(correct),
            cancellationToken: ct);
    }

}
