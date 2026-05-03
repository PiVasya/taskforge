# TaskForge Telegram Quiz Bot

Отдельный C#/.NET 10 микросервис для переноса старых Python Telegram-ботов учителя и ученика в TaskForge.

## Что внутри

- два Telegram-бота, как в старой схеме:
  - `TeacherBotHostedService`;
  - `StudentBotHostedService`;
- отдельная PostgreSQL-база микросервиса;
- EF Core + Npgsql;
- хранение изображений вопросов в существующем TaskForge MinIO/S3;
- схема БД повторяет старую SQLite-схему: `quizzes`, `whitelist`, `progress`, `categories`, `subcategory_stats`, `technical_break`, `diagnostic_*` и т.д.;
- импортёр старой SQLite-базы `LegacySqliteImportService`;
- `/health` для простой проверки процесса;
- `/ready` для проверки готовности сервиса и подключения к БД;
- нормальная обработка Telegram long polling timeout без огромных error stacktrace в логах.

## Важное по runtime

Dockerfile ставит системные зависимости, которые нужны в Debian runtime-образе:

```text
libgssapi-krb5-2
ca-certificates
tzdata
wget
```

`libgssapi-krb5-2` нужен, чтобы не получать в контейнере ошибку:

```text
Cannot load library libgssapi_krb5.so.2
```

`wget` нужен для Docker healthcheck.

## Миграции EF Core

В текущей ветке миграции уже могут быть закоммичены в `Data/Migrations`.

Если меняешь сущности, генерируй новую миграцию локально:

```bash
dotnet restore telegram-quiz-bot/TelegramQuizBot.csproj
dotnet tool install --global dotnet-ef --version 10.*
dotnet ef migrations add MigrationName \
  --project telegram-quiz-bot/TelegramQuizBot.csproj \
  --startup-project telegram-quiz-bot/TelegramQuizBot.csproj \
  --output-dir Data/Migrations
```

Если для генерации нужна конкретная строка подключения, можно временно задать:

```bash
export TELEGRAM_QUIZ_MIGRATION_CONNECTION="Host=localhost;Port=5432;Database=taskforge_telegram_quiz;Username=taskforge_telegram_quiz;Password=taskforge_telegram_quiz"
```

При запуске сервиса `DatabaseStartupService` сам применит уже сгенерированные миграции, если включено:

```env
TelegramQuiz__ApplyMigrationsOnStartup=true
```

## Конфигурация

Основные переменные окружения:

```env
TELEGRAM_QUIZ_TEACHER_BOT_TOKEN=
TELEGRAM_QUIZ_STUDENT_BOT_TOKEN=
TELEGRAM_QUIZ_TEACHER_PASSWORD=
TELEGRAM_QUIZ_ADMIN_USER_ID=0
TELEGRAM_QUIZ_REQUEST_TIMEOUT_SECONDS=600
```

База задаётся через:

```env
ConnectionStrings__TelegramQuizDb=Host=telegram-quiz-db;Port=5432;Database=...;Username=...;Password=...
```

MinIO используется существующий из TaskForge compose:

```env
S3__Endpoint=http://minio:9000
S3__Bucket=${S3_BUCKET}
S3__AccessKey=${S3_ACCESS_KEY}
S3__SecretKey=${S3_SECRET_KEY}
```

## Проверка на сервере

```bash
docker logs -f taskforge-telegram-quiz-bot

docker exec taskforge-telegram-quiz-bot wget -qO- http://localhost:8080/health

docker exec taskforge-telegram-quiz-bot wget -qO- http://localhost:8080/ready
```

## Безопасный прод-подход

На живом проде не используй:

```bash
docker compose down -v
docker compose up -d --remove-orphans
```

Пока идёт миграция на split-compose, поднимай новый сервис точечно:

```bash
docker compose -p taskforge-prod-linux -f docker-compose-prod-split.yml up -d telegram-quiz-db
docker compose -p taskforge-prod-linux -f docker-compose-prod-split.yml up -d telegram-quiz-bot
```

## Стадия переноса

Это первый структурный перенос: сервис, БД, Docker, autobuild, базовые команды учителя/ученика и каркас старой схемы.
Следующий этап — добить сложные сценарии старого Python-бота: inline-выбор категорий, диагностический режим, умный режим в полном объёме, экспорт и расширенный импорт картинок из старого архива.
