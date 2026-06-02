# taskforge-tasks/assignment-api-api

Настоящая граница микросервиса для домена `tasks/assignment-api`.

В папке `extracted/` лежат исходные контроллеры/сервисы/модели, вырезанные из старого `taskforge` API. Они оставлены не как legacy-runtime, а как исходник для переноса логики в этот сервис без потерь.

БД сервиса: `taskforge_tasks`.

EF Core migrations хранятся в этой папке и применяются владельцем сервиса.
