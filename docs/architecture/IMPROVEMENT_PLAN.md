# Improvement plan

Ниже список улучшений, которые стоит делать после того, как проект поднимется в новой микросервисной структуре.

## 1. Завершить перенос логики из `extracted/`

`extracted/` сейчас нужен, чтобы старая логика не потерялась. Он не компилируется в сервисы.

Следующий шаг — переносить код в настоящие слои сервисов:

```text
Domain
Application
Infrastructure
Api
```

Приоритет:

1. `identity` — auth/users/roles.
2. `solutions` — submissions, verdicts, leaderboard, badges, quotas.
3. `execution` — execution jobs, очередь, dispatch to runners.
4. `ai` — AgentRun/AgentMessage/AgentArtifact.
5. `support` и `minecraft`.

## 2. Миграции

Миграции хранить в репозитории по владельцам данных. Новые миграции генерировать безопасным скриптом `./scripts/generate-migrations.sh`, который сначала проверяет pending model changes.

Сейчас startup-migrate оставлен через `MIGRATE_ON_STARTUP=true`, но для Kubernetes лучше сделать отдельные migrator jobs:

```text
identity-migrator
solutions-migrator
execution-migrator
ai-migrator
```

## 3. Rating / leaderboard

Текущий правильный целевой подход:

```text
SolutionVerdictChanged event
  -> rating-worker
  -> UserRatings / LeaderboardEntries
  -> leaderboard reads prepared read model
```

Так не надо пересчитывать рейтинг на лету при каждом открытии страницы.

Дальше нужно:

- оформить событие `SolutionVerdictChanged` как реальный publish в RabbitMQ;
- сделать idempotent consumer в `rating-worker`;
- хранить checkpoint в `RatingProjectionCheckpoints`;
- добавить rebuild-команду для полного пересчёта рейтинга.

## 4. Execution service

Сейчас структура правильная:

```text
solutions-api -> execution-api -> queue -> execution-worker -> runner
```

Дальше нужно реализовать реальные durable jobs:

- `ExecutionJob`;
- `ExecutionResult`;
- retry/dead-letter queue;
- timeouts/limits;
- runner heartbeat;
- sandbox isolation;
- artifact storage через files-api/MinIO.

## 5. AI service

AI должен владеть своей БД `taskforge_ai`, а не жить в основном API.

Дальше нужно:

- вынести `AgentConversation`, `AgentRun`, `AgentStep`, `AgentArtifact` в `ai-api`;
- `ai-worker` должен работать через очередь;
- AI artifacts хранить через `files-api`;
- импорт AI-drafts в tasks/content делать через API профильных сервисов, а не прямой записью в чужую БД.

## 6. Production / HA

Для одного сервера split compose уже подготовлен.

Для нескольких серверов:

- PostgreSQL primary + standby replication;
- MinIO/S3 replication;
- RabbitMQ durable queues, затем federation/cluster;
- global failover через Cloudflare/DNS/LB;
- Kubernetes overlays по регионам: `rb`, `israel`, `poland`.

## 7. Observability

Добавить:

- structured logs;
- Prometheus metrics;
- Grafana dashboards;
- Loki logs;
- OpenTelemetry traces;
- health endpoints with dependencies: DB/RabbitMQ/MinIO.

## 8. Security

Особенно для code runners:

- non-root user;
- read-only root filesystem where possible;
- CPU/memory/pids limits;
- network isolation;
- seccomp/apparmor;
- no Docker socket;
- per-run temp directory cleanup;
- kill process tree on timeout.
