namespace TelegramQuizBot.Bot;

public static class TelegramText
{
    public static string TeacherHelp => string.Join('\n',
        "📚 Доступные команды учителя:",
        "/add_user [id] [часы] — добавить ученика",
        "/remove_user [id] — удалить ученика",
        "/add_quiz — добавить quiz-опрос",
        "/add_text_question — добавить текстовый вопрос",
        "/remove_quiz [id] — удалить вопрос",
        "/add_category [name] — добавить категорию",
        "/remove_category [name] — удалить категорию",
        "/list_categories — список категорий",
        "/list_quizzes — список вопросов",
        "/class_stats — статистика класса",
        "/log_start — лог запусков",
        "/technical_break [on/off] [ЧЧ:ММ] — тех. перерыв",
        "/logout — выход",
        "/help — справка");

    public static string StudentHelp => string.Join('\n',
        "📚 Команды ученика:",
        "/start — начать работу",
        "/next — следующий вопрос",
        "/stats — моя статистика",
        "/help — справка");
}
