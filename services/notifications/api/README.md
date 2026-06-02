# taskforge-notifications-api

Настоящая граница микросервиса для домена `notifications`.

В папке `extracted/` лежат исходные контроллеры/сервисы/модели, вырезанные из старого `taskforge` API. Они оставлены не как legacy-runtime, а как исходник для переноса логики в этот сервис без потерь.

БД сервиса: `taskforge_notifications`.

EF Core migrations хранятся в этой папке и применяются владельцем сервиса.
