# Миграционная точка SQL: исходники подготовлены, миграции нужно создать у себя

**Это промежуточный полный source archive, а не production-обновление.**
Не запускай `./build.sh`, не разворачивай это на A/B/C и не пушь в ветку
с автопубликацией образов до возврата и проверки миграций.
Все команды ниже выполняются локально из корня распакованного архива.

## EF runtime gate

This package explicitly references `Microsoft.EntityFrameworkCore` and
`Microsoft.EntityFrameworkCore.Relational` **10.0.8** in all three API projects.
`Microsoft.EntityFrameworkCore.Design` remains private. The Npgsql provider is
unchanged at **10.0.2**. Runtime packages must propagate through ProjectReference;
private design-time dependencies must not be used as implicit runtime version pins.

The check script validates project references, restores NuGet, inspects all four
real `obj/project.assets.json` graphs, builds with assembly-conflict warnings treated
as errors, and runs 44 model/domain checks. It never starts the APIs or generates
or applies migrations. The prepared C# checks have NOT been run by the assistant.
Current evidence: `./docs/sql/QA_EF_RUNTIME_FIX.md` (paths here are from the source root).
Extract into a fresh directory so old build outputs and old root-level docs do not remain.

## 1. Сначала сборка и проверка моделей

Нужен .NET 10 SDK. Локальный EF CLI зафиксирован на 10.0.8,
как и Microsoft.EntityFrameworkCore.Design в исходных csproj.
В среде ассистента SDK отсутствует: C# build и 44 .NET-проверки
здесь не выполнены. Это не заменяется статическим аудитом.

```bash
bash ./scripts/check-sql-domain.sh
```

При любой ошибке остановись и пришли лог. Миграции пока не создавай.
Model-check не подключается к БД и не генерирует миграции.

## 2. Три миграции

```bash
dotnet ef migrations add AddSqlTaskDomain \
  --project ./services/tasks/assignment-api/TaskForge.Tasks.Api.csproj \
  --startup-project ./services/tasks/assignment-api/TaskForge.Tasks.Api.csproj \
  --context TasksDbContext \
  --configuration Release \
  --output-dir Migrations

dotnet ef migrations add AddExecutionJobRouting \
  --project ./services/execution/api/TaskForge.Execution.Api.csproj \
  --startup-project ./services/execution/api/TaskForge.Execution.Api.csproj \
  --context ExecutionDbContext \
  --configuration Release \
  --output-dir Migrations

dotnet ef migrations add AddSqlSubmissionBinding \
  --project ./services/solutions/api/TaskForge.Solutions.Api.csproj \
  --startup-project ./services/solutions/api/TaskForge.Solutions.Api.csproj \
  --context SolutionsDbContext \
  --configuration Release \
  --output-dir Migrations
```

После каждой команды проверяй успех. Не продолжай при ошибке.
Не запускай `dotnet ef database update`, не удаляй историю миграций.
EF сам создаст новые файлы и обновит три ModelSnapshot: это ожидаемо.
Ассистент эти файлы не создавал и не менял.

## 3. Проверка и возврат

```bash
bash ./scripts/check-sql-domain.sh
python3 ./scripts/ci/check-sql-migration-boundary.py --after-user-migrations
```

Пришли проект целиком с тремя новыми миграциями,
их Designer-файлами и обновлёнными snapshots.
`bin/`, `obj/`, `node_modules/`, `.git/` и секреты в архив не нужны.

Expected migration review:

- Tasks: eight new SQL tables, their FK/unique/check constraints and indexes.
  Existing Assignment/test/math/image/analytics tables must not be dropped or recreated.
- Execution: eight added routing/lease columns; SubmissionId widened to nullable;
  legacy default, claim/deduplication indexes and check constraints. Existing job data remains.
- Solutions: three nullable binding columns, an index and a binding check constraint.
  Existing submissions, ratings and progression remain.

The factories use Npgsql exactly as the runtime does, but do not execute Program.cs.
The default tooling connection points at an unused loopback port. A running database,
Docker, Redis, RabbitMQ or production credentials are not required for model generation.
`TASKFORGE_EF_CONNECTION` is an optional **tooling-only** override, not a new cluster setting.

Implementation details: `./docs/sql/DOMAIN_STAGE.md`.
Current checks/limitations: `./docs/sql/QA_EF_RUNTIME_FIX.md`.
Historical stage-1 record: `./docs/sql/QA_STAGE1.md`.
Original plan: `./docs/sql/SQL_UPDATE_PLAN_TASKFORGE.txt`.
Production r57 remains unchanged. The SQL worker/API/UI/CI/Compose and a self-contained
new cluster revision are the next stage, after reviewing the user-generated migrations.
