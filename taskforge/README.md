# taskforge — основное API

ASP.NET Core + EF Core (PostgreSQL). Управляет пользователями, курсами, заданиями, решениями и судейством.

## Быстрый старт (локально)
```bash
cd taskforge
dotnet restore
dotnet ef database update  # если нужно накатить миграции
dotnet run --project taskforge.csproj
# API поднимется на http://localhost:8080 (или как задано в launchSettings/appsettings)
```

Либо через общий `docker-compose.local.yaml` (рекомендуется, поднимет Postgres, nginx и раннеры).

## Конфигурация
- `appsettings.json` — строки подключения, URL раннеров:
  - `Compiler:CppRunnerUrl` → `http://cpp-runner:8080`
  - `Compiler:CsharpRunnerUrl` → `http://csharp-runner:8080`
  - `Compiler:PythonRunnerUrl` → `http://python-runner:8080`
- JWT секрет/параметры — в `appsettings.*` или переменных окружения.

## Основные сущности
- Users, Courses, Assignments (`Type: "code-test"`), TaskTestCases, UserTaskSolutions.

## Эндпоинты (сокращённо)
- `POST /api/auth/register`, `POST /api/auth/login`
- `GET /api/profile` / `PUT /api/profile` — профиль текущего пользователя
- `POST /api/courses` / `GET /api/courses` / `GET|PUT|DELETE /api/courses/{courseId}`
- `POST /api/courses/{courseId}/assignments`
- `GET /api/courses/{courseId}/assignments`
- `GET|PUT|DELETE /api/assignments/{assignmentId}`
- `POST /api/assignments/{assignmentId}/submit` — сабмит решения (официальная проверка со скрытыми тестами)
- `POST /api/compiler/compile-run` — одиночный прогон (без скрытых тестов)
- `POST /api/compiler/run-tests` — прогон набора публичных тестов
- `POST /api/judge/run` — единая «судья» логика (используется сабмитом)
- `GET /api/admin/leaderboard` и выборки по пользователям/решениям (админ)

## Судейство и сравнение вывода
- Нормализация перевода строк (`\r\n` → `\n`), обрезание хвостовых пробелов по строкам.
- Финальная пустая строка в фактическом выводе игнорируется.

## Интеграция с раннерами
- Языки: `cpp`, `csharp`, `python`.
- Сервис `CompilerController` маршрутизирует код к раннерам согласно языку.

## Сборка/деплой
- `dockerfile` в корне проекта `taskforge/`.
- Используйте `docker-compose.local.yaml` для полного окружения (nginx, api, runners, postgres).
