# Target microservice split

Этот архив режет старый `taskforge` API по доменам. Старый огромный API больше не считается владельцем всей системы.

## Сервисы и БД

| Service | DB | Notes |
|---|---|---|
| `identity-api` | `taskforge_identity` | users, auth, roles, UI settings |
| `education-api` | `taskforge_education` | courses, groups, visibility, ownership |
| `content-api` | `taskforge_content` | learning pages, conspects, content tree |
| `tasks-api` | `taskforge_tasks` | assignments, programming/math/test task definitions |
| `quiz-api` | `taskforge_tasks` | CT/quiz tasks; later can merge behind tasks-api |
| `solutions-api` | `taskforge_solutions` | submissions, attempts, verdicts, quotas, badges, rating read-model |
| `rating-worker` | `taskforge_solutions` | updates `UserRatings` and `LeaderboardEntries` from events |
| `execution-api` | `taskforge_execution` | execution jobs/results/runner registry |
| `execution-worker` | none | consumes queue and calls stateless runners |
| `runners/*` | none | stateless code runners |
| `ai-api` | `taskforge_ai` | AI conversations/runs/steps/artifacts/drafts |
| `ai-worker` | none | executes AI runs; state belongs to `ai-api` |
| `support-api` | `taskforge_support` | tickets/messages |
| `minecraft-api` | `taskforge_minecraft` | link codes, economy, chat bridge |
| `files-api` | `taskforge_files` | file metadata; bytes in MinIO/S3 |
| `notifications-api` | `taskforge_notifications` | notification outbox/subscriptions |
| `observability-api` | `taskforge_observability` | request logs, user action logs, system status |

## Что произошло с исходниками старого API

Исходники не выброшены. По каждому домену они лежат в `services/<domain>/api/extracted/`.

Это нужно, чтобы переносить реальную логику из старого `taskforge` API без потери поведения, но уже в правильные владельцы данных.

## Миграции

Миграции хранятся в репозитории по владельцам данных. Каждый DB-owning сервис имеет собственную папку `Migrations` и собственный `ModelSnapshot`.

Текущий baseline: `InitialMicroserviceSchema`.

Новые изменения схемы добавляются через безопасный скрипт:

```bash
./scripts/generate-migrations.sh AddMeaningfulSchemaChange
```

## Рейтинг

Старый подход: открыть рейтинг -> прочитать много решений -> пересчитать на лету.

Новый подход:

1. `solutions-api` фиксирует вердикт решения.
2. Публикуется `SolutionVerdictChanged`.
3. `rating-worker` обновляет `UserRatings` и `LeaderboardEntries`.
4. UI читает готовую read-model таблицу.

Так рейтинг становится быстрым, кэшируемым и не грузит БД полным пересчётом при каждом входе.
