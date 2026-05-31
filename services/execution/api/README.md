# taskforge-execution/api-api

Настоящая граница микросервиса для домена `execution/api`.

В папке `extracted/` лежат исходные контроллеры/сервисы/модели, вырезанные из старого `taskforge` API. Они оставлены не как legacy-runtime, а как исходник для переноса логики в этот сервис без потерь.

БД сервиса: `taskforge_execution`.

Миграции не сгенерированы по требованию. См. `MIGRATIONS_REQUIRED.md`.
