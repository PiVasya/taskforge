# Legacy SQLite -> Telegram Quiz PostgreSQL

Старый бот использовал SQLite и таблицы без нормальной структуры проекта. В новом микросервисе схема сохранена по смыслу и именам таблиц, но перенесена в PostgreSQL через EF Core.

| Старая таблица SQLite | Новая таблица PostgreSQL | Entity |
|---|---|---|
| `authorized_teachers` | `authorized_teachers` | `AuthorizedTeacher` |
| `whitelist` | `whitelist` | `WhitelistEntry` |
| `start_log` | `start_log` | `StartLogEntry` |
| `progress` | `progress` | `ProgressEntry` |
| `quizzes` | `quizzes` | `QuizQuestion` |
| `categories` | `categories` | `Category` |
| `category_stats` | `category_stats` | `CategoryStat` |
| `subcategory_stats` | `subcategory_stats` | `SubcategoryStat` |
| `smart_progress` | `smart_progress` | `SmartProgressEntry` |
| `user_settings` | `user_settings` | `UserSetting` |
| `technical_break` | `technical_break` | `TechnicalBreak` |
| `diagnostic_results` | `diagnostic_results` | `DiagnosticResult` |
| `diagnostic_tests` | `diagnostic_tests` | `DiagnosticTest` |
| `test_questions` | `test_questions` | `TestQuestion` |
| `user_answers` | `user_answers` | `UserAnswer` |

## Важные отличия

- `diagnostic_results` в старой реальной базе был повреждённо спроектирован: `subcategory` оказался primary key без `user_id`. В новом сервисе ключ сделан человеческим: `(user_id, subcategory)`.
- `quizzes.options` пока сохранён как строка с разделителем `|`, чтобы импорт из старой базы был прямым. Позже можно вынести варианты ответов в отдельную таблицу.
- `quizzes.image` теперь хранит не локальный путь, а S3/MinIO key.
- `technical_break` сохранён как single-row table с `id = 1`.
