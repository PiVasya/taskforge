# TaskForge Telegram Quiz Bot

C#/.NET 10 микросервис Telegram-ботов для учебных quiz-заданий TaskForge.

## Схема

Сервис повторяет старую схему из Python-проекта:

- teacher bot — помощник учителя;
- student bot — бот ученика;
- отдельная PostgreSQL-база сервиса;
- изображения вопросов — в существующий MinIO/S3;
- EF Core migrations генерируются разработчиком и применяются `Program.cs` при старте.

## Важное про миграции

В репозитории намеренно нет готовых миграций. После изменения моделей надо выполнить:

```bash
cd telegram-quiz-bot

dotnet restore

dotnet tool update --global dotnet-ef --version 10.*

dotnet ef migrations add AddStudentContacts \
  --project TelegramQuizBot.csproj \
  --startup-project TelegramQuizBot.csproj \
  --output-dir Data/Migrations
```

После генерации миграции надо закоммитить `Data/Migrations/**`. В контейнере `Program.cs` применит миграции при старте, если включено:

```env
TELEGRAM_QUIZ_APPLY_MIGRATIONS_ON_STARTUP=true
```

## Student contacts

Новая нормальная таблица:

```text
student_contacts
```

Она хранит всех, кто писал student-боту:

- Telegram ID;
- chat ID;
- username;
- имя/фамилию/full name;
- язык;
- первое и последнее обращение;
- тип и текст последнего сообщения;
- количество сообщений;
- search_text для быстрого поиска;
- флаг скрытия контакта.

`start_log` больше не используется как справочник учеников. Он остаётся историческим логом `/start`.

## Команды teacher bot по ученикам

```text
/students
/students запрос
/find_student запрос
/search_student запрос
/student user_id
/grant_user user_id [hours]
/revoke_user user_id
/student_clean
```

Поиск работает по:

- Telegram ID;
- username;
- имени;
- фамилии;
- full name;
- частичному совпадению;
- токенам;
- расстоянию Левенштейна.

## Очистка

```text
/student_clean logs [days]
/student_clean denied [days]
/student_clean hidden [days]
/student_clean contact user_id
/student_clean contact user_id access
/student_clean contact user_id progress logs
/student_clean rebuild
```

`rebuild` полезен после импорта старой SQLite-базы: он заполняет `student_contacts` из `whitelist`, `progress` и `start_log`.

## Teacher access

`AdminUserId` больше не используется.

Любой помощник учителя может войти по паролю teacher-бота. После входа его Telegram ID сохраняется в `authorized_teachers`.

## Импорт старой SQLite-базы

Один файл:

```bash
dotnet run -- --import-old-sqlite /path/to/teacher_bot.db
```

Папка с несколькими `.db/.sqlite/.sqlite3`:

```bash
dotnet run -- --import-old-sqlite /path/to/legacy-dbs
```

Импортёр:

- пропускает отсутствующие таблицы;
- переносит старые категории, вопросы, whitelist, учителей, прогресс, настройки, диагностику, ответы;
- после импорта перестраивает `student_contacts`.
