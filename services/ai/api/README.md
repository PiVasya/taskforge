# taskforge-ai/api-api

Настоящая граница микросервиса для домена `ai/api`.

В папке `extracted/` лежат исходные контроллеры/сервисы/модели, вырезанные из старого `taskforge` API. Они оставлены не как legacy-runtime, а как исходник для переноса логики в этот сервис без потерь.

БД сервиса: `taskforge_ai`.

Миграции не сгенерированы по требованию. См. `MIGRATIONS_REQUIRED.md`.

## Internal worker compatibility endpoints

The API exposes `/api/internal/agent/*` endpoints expected by the extracted `.NET` AI worker.
At this stage `claim-next` returns an empty queue (`job: null`) so the worker stays healthy without spamming 404 logs.
Real AI job dispatch should be implemented inside this service later, with `taskforge_ai` as the owner database.
