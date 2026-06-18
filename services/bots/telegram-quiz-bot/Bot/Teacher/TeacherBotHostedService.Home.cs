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
    private static async Task<bool> HandleTeacherButtonAsync(
        ITelegramBotClient bot,
        long chatId,
        string text,
        QuizService quizzes,
        StudentDirectoryService directory,
        StatisticsService stats,
        CancellationToken ct)
    {
        switch (text)
        {
            case "🏠 Меню":
                await SendTeacherHomeAsync(bot, chatId, ct);
                return true;
            case "📚 Квизы":
                await SendQuizListAsync(bot, chatId, null, quizzes, 1, 0, 0, ct);
                return true;
            case "👥 Ученики":
                await SendStudentListAsync(bot, chatId, directory, null, ct);
                return true;
            case "➕ Вопрос":
                await bot.SendTextMessageAsync(
                    chatId,
                    "<b>➕ Добавление вопроса</b>\n\nВыбери тип добавления ниже.",
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("📝 Текстовый вопрос", "tm:addText") },
                        new[] { InlineKeyboardButton.WithCallbackData("📊 Telegram quiz-опрос", "tm:addQuizInfo") },
                        new[] { InlineKeyboardButton.WithCallbackData("🏠 В меню", "tm:home") }
                    }),
                    cancellationToken: ct);
                return true;
            case "📊 Статистика":
                await bot.SendTextMessageAsync(chatId, await stats.BuildClassStatsAsync(ct), replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
                return true;
            case "🧹 Очистка":
                await bot.SendTextMessageAsync(chatId, TelegramText.StudentCleanHelp, replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
                return true;
            case "❓ Помощь":
                await bot.SendTextMessageAsync(chatId, TelegramText.TeacherHelp, replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
                return true;
            default:
                return false;
        }
    }

    private static async Task SendTeacherHomeAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        var text = string.Join('\n',
            "<b>🏠 Панель учителя</b>",
            "",
            "Теперь основной сценарий работает через кнопки.",
            "Команды оставлены как быстрые шорткаты, но пользоваться ими не обязательно.",
            "",
            "<b>Главное:</b>",
            "📚 Квизы — список, страницы, категории, карточки вопросов.",
            "👥 Ученики — кто писал student-боту и кому выдать доступ.",
            "➕ Вопрос — добавление нового задания.",
            "📊 Статистика — прогресс класса.");

        await bot.SendTextMessageAsync(
            chatId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: TeacherMainKeyboard(),
            cancellationToken: ct);
    }

    private static ReplyKeyboardMarkup TeacherMainKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("📚 Квизы"), new KeyboardButton("👥 Ученики") },
            new[] { new KeyboardButton("➕ Вопрос"), new KeyboardButton("📊 Статистика") },
            new[] { new KeyboardButton("🧹 Очистка"), new KeyboardButton("❓ Помощь") },
            new[] { new KeyboardButton("🏠 Меню") }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false
        };
    }

    private const int QuizPageSize = 20;
    private const int PickerPageSize = 8;
}
