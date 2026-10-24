# Аналитика задания: графики, лента решений и защита EF-миграций

Дата: 2026-10-24. Основа: `taskforge-develop(323).zip`.

## Аналитика

- На `/admin/assignments/:assignmentId/insights` добавлены графики успешных,
  неуспешных и ещё проверяемых попыток. Кольцевая диаграмма использует точные
  суммарные счётчики, а динамика — SQL-агрегаты за последние 30 дней (переключатели
  7/14/30 дней). Не подменять общие значения последними 150/250/1000 решениями.
- В `solutions-api` внутренний endpoint `attempts-summary` добавляет `dailyAttempts`
  и `pendingCodeAttempts`; `tasks-api` объединяет агрегаты C++/SQL/изображений
  с тестами и математикой. Из БД извлекаются только агрегированные количества.
- Новая вкладка **«Лента решений»** повторно использует действующий
  `/api/admin/solution-events` (SSE, keepalive, Last-Event-ID, Redis broker).
  Фильтрация по `assignmentId` происходит до загрузки деталей. Исторические
  записи объединяются с SSE по `(тип, id)`, статусы обновляются без дубликатов;
  код, тесты, картинки и математика раскрываются штатной карточкой решений.
- Поведенческие события оставлены в **«Контроле поведения»** на существующем
  SignalR-канале, который не подменяет SSE с решениями. Записи поведения больше
  не занимают главную ленту.
- Интерфейс переведён на русский: режимы, типы задач, ошибки проверки,
  контроль поведения, метки активности и пояснения.
- Графики фоново освежаются раз в 60 секунд при доступном SSE; без запроса
  полной истории на каждое событие.

## Почему возникли две одинаковые миграции

В предыдущей поставке миграция `20261024153108_AddRatingAcceptedActivities`
была зафиксирована вместе со старым `SolutionsDbContextModelSnapshot`. При следующем
запуске EF увидел различие модели и snapshot и сгенерировал
`20261024160137_AddRatingAcceptedActivities2`. Обе операции `Up()` создавали
`RatingAcceptedActivities`. Проверка только `has-pending-model-changes` могла
пройти после обновления snapshot второй миграцией, не обнаружив дубликата DDL.

### Защита

- `scripts/ci/check-ef-migration-history.py` отклоняет повторный `CreateTable`
  без промежуточного `DropTable`, а также несовпадение последнего Designer
  с текущим `ModelSnapshot`.
- `generate-migrations.sh`, `generate-migrations-force.sh` проверяют историю
  **до** вызова EF; CI тоже запускает защиту **до** dotnet restore/build.
- Регрессионные фикстуры в `scripts/ci/test-ef-migration-check.py` покрывают
  устаревший snapshot и дублирующее создание таблицы.
- **Никакие EF миграции/Designer/ModelSnapshot в этом архиве не созданы и не
  изменены.** Новая таблица для графиков не требуется.

## Порядок проверки

1. `python3 scripts/ci/test-ef-migration-check.py`
2. `python3 scripts/ci/check-ef-migration-history.py --dir services/solutions/api/Migrations`
3. `dotnet test services/tasks/assignment-api/tests/TaskForge.Tasks.Api.Tests/TaskForge.Tasks.Api.Tests.csproj -c Release`
4. `dotnet test services/solutions/api/tests/TaskForge.Solutions.Api.Tests/TaskForge.Solutions.Api.Tests.csproj -c Release`
5. `cd apps/web && CI=true npm test -- --watchAll=false assignmentInsightsModel.test.js`
6. Сборка `apps/web` и CI `develop-build.yml`.

Из-за отсутствия `.NET SDK` и зависимостей frontend в среде упаковки полноценные
сборки .NET/React здесь не выполнялись. Статические проверки и сценарии
JS-модели/защиты EF выполнены отдельно; production deployment не проводился.

## Обновляемые сервисы

`tasks-api`, `solutions-api`, `front` (остальные сервисы и серверное управление
v41.5.9 не требуют обновления). Новые EF-миграции не нужны.
