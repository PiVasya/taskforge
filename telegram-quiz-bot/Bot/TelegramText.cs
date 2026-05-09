namespace TelegramQuizBot.Bot;

public static class TelegramText
{
    public static string TeacherHelp => string.Join('\n',
        "📚 Панель учителя теперь работает через кнопки.",
        "",
        "Основные кнопки:",
        "📚 Квизы — список вопросов, страницы, фильтры по категориям и карточки.",
        "👥 Ученики — список тех, кто писал student-боту, выдача и отзыв доступа.",
        "➕ Вопрос — добавление нового вопроса.",
        "📊 Статистика — статистика класса.",
        "🧹 Очистка — очистка учеников/логов и перестройка справочника.",
        "🏠 Меню — вернуться в главное меню.",
        "",
        "Старые команды оставлены как быстрые шорткаты, но обычный сценарий теперь кнопочный.");

    public static string StudentCleanHelp => string.Join('\n',
        "🧹 Очистка учеников и логов:",
        "",
        "Пока опасные действия очистки оставлены командами, чтобы случайно не удалить данные кнопкой.",
        "",
        "/student_clean logs [days] — удалить start_log старше N дней. По умолчанию 90.",
        "/student_clean denied [days] — удалить контакты без активного доступа старше N дней. По умолчанию 30.",
        "/student_clean hidden [days] — удалить скрытые контакты старше N дней. По умолчанию 30.",
        "/student_clean contact user_id — удалить только карточку ученика из student_contacts.",
        "/student_clean contact user_id access — удалить карточку и whitelist-доступ.",
        "/student_clean contact user_id progress logs — удалить карточку, прогресс и логи ученика.",
        "/student_clean rebuild — перестроить student_contacts из whitelist, progress и start_log после импорта старой БД.");

    public static string StudentHelp => string.Join('\n',
        "📚 Команды ученика:",
        "/start — начать работу",
        "/next — следующий вопрос",
        "/stats — моя статистика",
        "/help — справка");
}
