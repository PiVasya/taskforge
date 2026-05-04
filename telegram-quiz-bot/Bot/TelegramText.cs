namespace TelegramQuizBot.Bot;

public static class TelegramText
{
    public static string TeacherHelp => string.Join('\n',
        "📚 Доступные команды учителя:",
        "",
        "👥 Ученики и доступ:",
        "/students — последние ученики, писавшие student-боту",
        "/students запрос — fuzzy-поиск по ID, username, имени и фамилии",
        "/student user_id — карточка ученика",
        "/grant_user user_id [часы] — выдать доступ; без часов = навсегда",
        "/revoke_user user_id — забрать доступ",
        "/student_clean — справка по очистке учеников/логов",
        "",
        "🧠 Вопросы:",
        "/add_quiz — добавить quiz-опрос",
        "/add_text_question — добавить текстовый вопрос",
        "/remove_quiz id — удалить вопрос",
        "/list_quizzes — список вопросов",
        "",
        "📁 Категории:",
        "/add_category name — добавить категорию",
        "/remove_category name — удалить категорию",
        "/list_categories — список категорий",
        "",
        "📊 Система:",
        "/class_stats — статистика класса",
        "/log_start — лог запусков",
        "/technical_break [on/off] [ЧЧ:ММ] — тех. перерыв",
        "/logout — выход",
        "/help — справка");

    public static string StudentCleanHelp => string.Join('\n',
        "🧹 Очистка учеников и логов:",
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
