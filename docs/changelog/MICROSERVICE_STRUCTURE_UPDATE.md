# Microservice structure update

Что исправлено:

- API `Program.cs` больше не содержит маршруты, DTO и вспомогательные методы в одном файле.
- Маршруты вынесены в `Endpoints/` и разложены по доменным областям.
- HTTP-контракты вынесены в `Contracts/`.
- SignalR-хабы вынесены в `Hubs/`.
- Добавлен документ `docs/architecture/microservice-source-layout.md` с правилом будущей структуры.

Затронутые API-сервисы:

- `services/tasks/assignment-api`
- `services/solutions/api`
- `services/identity/api`
- `services/ai/api`
- `services/content/api`
- `services/tasks/quiz-api`
- `services/observability/api`
- `services/files/api`
- `services/education/api`
- `services/support/api`
- `services/minecraft/api`
- `services/execution/api`
- `services/notifications/api`

Важно: это был первичный этап. Следующий cleanup удалил `extracted/` и перенёс helper/support-файлы из `Endpoints/` в `Services/`.
