# Legacy SQLite -> Telegram Quiz PostgreSQL mapping

## Основные таблицы

| Legacy SQLite | PostgreSQL / EF Core | Комментарий |
|---|---|---|
| `authorized_teachers` | `authorized_teachers` | Помощники учителя. Вход по паролю, без `AdminUserId`. |
| `whitelist` | `whitelist` | Доступ учеников: навсегда или до `expire_time`. |
| `start_log` | `start_log` | Исторический лог `/start`, не справочник учеников. |
| `start_log`, `whitelist`, `progress` | `student_contacts` | Новый нормальный справочник всех, кто писал student-боту или был найден при импорте. |
| `quizzes` | `quizzes` | Вопросы, ответы, категории, подкатегории, image key. |
| `categories` | `categories` | Категории. |
| `progress` | `progress` | Общий прогресс ученика. |
| `category_stats` | `category_stats` | Статистика по категориям. |
| `subcategory_stats` | `subcategory_stats` | Статистика по подкатегориям. |
| `smart_progress` | `smart_progress` | Данные умного режима. |
| `user_settings` | `user_settings` | Настройки ученика. |
| `technical_break` | `technical_break` | Технический перерыв. |
| `diagnostic_results` | `diagnostic_results` | Результаты диагностики. |
| `diagnostic_tests` | `diagnostic_tests` | Диагностические тесты. |
| `test_questions` | `test_questions` | Связь тестов и вопросов. |
| `user_answers` | `user_answers` | История ответов. |

## student_contacts

Новая таблица нужна, чтобы teacher bot мог по-человечески показывать всех учеников, которые писали student bot, искать их и выдавать доступ кнопкой.

| Column | Meaning |
|---|---|
| `user_id` | Telegram user ID, primary key. |
| `chat_id` | Chat ID для личного чата. Обычно равен user ID. |
| `username` | Telegram username без `@`. |
| `first_name`, `last_name`, `full_name` | Имя из Telegram. |
| `language_code` | Язык профиля Telegram. |
| `first_seen_at` | Когда ученик впервые написал student-боту. |
| `last_seen_at` | Когда ученик последний раз писал student-боту. |
| `last_message_at` | Время последнего сообщения. |
| `last_message_type` | Тип последнего Telegram-сообщения. |
| `last_message_text` | Текст или caption последнего сообщения. Ограничен по длине. |
| `message_count` | Сколько сообщений ученик отправил student-боту. |
| `is_hidden` | Скрыть из общего списка без удаления истории. |
| `hidden_at`, `hidden_by_teacher_id` | Кто и когда скрыл контакт. |
| `note` | Зарезервировано под заметки учителя. |
| `search_text` | Нормализованный индекс для fuzzy-поиска. |
