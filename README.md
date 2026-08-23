> Перед правками ИИ должен открыть [`00_AI_READ_THIS_FIRST.md`](./00_AI_READ_THIS_FIRST.md).

# TaskForge

Dev-baseline:

```text
PostgreSQL: 18-alpine
.NET:        net10.0 / SDK 10.0.300+
C#:          14.0
```

## Локальный запуск

Обычный сценарий теперь один:

```bash
chmod +x build.sh deploy/dev/compose.sh scripts/*.sh
./build.sh
```

`./build.sh` делает так:

```text
1. создаёт deploy/dev/.env из .env.example, если файла нет;
2. билдит сервисы по одному;
3. на ошибке показывает лог только упавшего сервиса;
4. если билд успешный, запускает стек без повторного build;
5. показывает статус контейнеров и полезные startup-логи.
```

Gateway по умолчанию:

```text
http://localhost:18080
```

Если нужен порт `8080`, поменяй в `deploy/dev/.env`:

```text
DEV_GATEWAY_HTTP_PORT=8080
```

## Удобные команды

Собрать всё и запустить:

```bash
./build.sh
```

Собрать и перезапустить только один сервис:

```bash
./build.sh tasks-api
```

Собрать несколько сервисов:

```bash
./build.sh tasks-api execution-worker
```

Собрать judge-цепочку:

```bash
./build.sh judge
```

Посмотреть основные логи:

```bash
./build.sh logs
```

Посмотреть логи judge-цепочки:

```bash
./build.sh logs judge
```

Запустить уже собранное без build:

```bash
./build.sh up
```

Остановить:

```bash
./build.sh down
```

Очистить локальные `bin/obj` и build-логи:

```bash
./build.sh clean
```

Проверить полный путь отправки решения после запуска:

```bash
./build.sh e2e
```

## Что делать, если билд упал

Например, упал `tasks-api`. В конце вывода будет:

```text
BUILD FAILED: tasks-api
Full log: deploy/dev/build-logs/<run>/build-tasks-api.log
Repeat only this service: ./build.sh tasks-api
```

Дальше чинишь ошибку и запускаешь только его:

```bash
./build.sh tasks-api
```

Весь зоопарк заново пересобирать не надо.

## Структура

```text
apps/                 web, web-ct, gateway
services/             доменные сервисы, workers, runners, analyzers
contracts/            HTTP/event contracts
infrastructure/       postgres init, k8s заготовки
deploy/               dev/prod compose окружения
scripts/              e2e и миграционные скрипты
```

## Важно

- Запуск кода идёт через durable path: `solutions-api` создаёт submission и execution job, `execution-worker` вызывает `code-analyzer` и runner, затем возвращает verdict обратно в `solutions-api`.
- Обычные code-задачи поддерживают 6 языков: `cpp`, `csharp`, `java`, `javascript`, `pascal`, `python`.
- Python application runtime разрешён только для `services/analyzers/image-analyzer`. Python остаётся языком решений на сайте, но `python-runner` как backend-сервис написан на Go и только запускает `python3` как инструмент исполнения пользовательского кода.
- Старые монолитные снапшоты из рабочего дерева убраны. Активный код сервиса хранится в `Endpoints/`, `Services/`, `Contracts/`, `Domain/`, `Data/`, `Infrastructure/`.

## Production deploy

Production compose lives in `deploy/prod`. If the environment is already configured, deploy is one command:

```bash
./deploy/prod/deploy.sh
```

First server bootstrap can also be one command by passing public values. The script creates `deploy/prod/.env`, generates strong random secrets, validates the config, pulls GHCR images and starts the stack:

```bash
IMAGE_REPOSITORY=ghcr.io/OWNER/REPO \
DOMAIN=taskforge.by \
CT_DOMAIN=ct.taskforge.by \
LETSENCRYPT_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_EMAILS=admin@example.com \
S3_PUBLIC_ENDPOINT=https://s3.taskforge.by \
./deploy/prod/deploy.sh
```

Security checks before startup:

```bash
scripts/prod/check-prod-config.sh
```
## AI accounts and Browser API

TaskForge exposes the already deployed React frontends to automated agents through a real, server-side Chromium service. There are no special share links and no hidden AI role. An AI account is an ordinary user with `AccountType=ai`; the marker is self-declared and grants no additional authorization. The current resource policy intentionally gives AI accounts unlimited task-solving energy and higher per-user task/browser throughput, while normal role checks and leaderboard/top-energy rules remain unchanged.

Public discovery and inspection:

```text
/.well-known/taskforge-ai.json
/.well-known/taskforge-ai-browser.json
/llms.txt
/ai-access
/ai-browser
/api/site/agent/playbook
/api/browser/openapi.json
/api/site/info
/api/site/routes
/api/site/snapshot
/api/site/render
/api/site/render.pdf
/api/browser/sessions
/api/ai/browser/start
```

Browser API contract version is `1.3`; semantic snapshot version is `2.2`. Test/math snapshots expose stable question/answer keys so agents do not have to rely on visual radio labels. For reliable serial solving, use Browser API for discovery/navigation and ordinary authenticated submit/result APIs as the authoritative mutation/verdict channel; reconcile an uncertain submit through the matching GET before retrying.

The browser service accepts only configured TaskForge sites plus relative paths, is protected by Nginx and Redis quotas, and cannot be used as a general-purpose URL proxy. See [`services/browser/api/README.md`](./services/browser/api/README.md) and [`00_AI_READ_THIS_FIRST.md`](./00_AI_READ_THIS_FIRST.md).

The Identity migration for the AI account marker already exists as `services/identity/api/Migrations/20260807225514_AddAiAccountType.cs`. Do not generate a second `AddAiAccountType` migration.

## Development logs

TaskForge is still in development, so Docker images and Compose services use detailed diagnostics by default:

```yaml
TASKFORGE_BUILD_DEBUG_LOGS: "1"
```

```dotenv
TASKFORGE_DEBUG_LOGS=1
```

Do not switch these values to `0` or remove the development probes unless the user explicitly requests a logging-policy change. Details: `docs/operations/development-logging.md`.


## Image-test v2 MinIO

Image-test expected images are stored in MinIO/S3 via files-api. `TestsJson` stores only input/output/threshold/hidden metadata and image keys/URLs. Current behavior is defined by `services/tasks/assignment-api/Services/Image` and the files service; do not restore binary image payloads into task JSON.


## Minecraft link-code delivery

Production sends account-link codes directly from `minecraft-api` to `http://mc.taskforge.by:25566/taskforge/link/send`. The browser never receives or displays a fallback code. Configure matching `MINECRAFT_WEBHOOK_KEY` / `security.taskforgeKey` values and keep TCP port `25566` reachable from the site server. Startup and delivery diagnostics are logged without revealing raw secrets or one-time codes.

## Minecraft multiple links and balance

A TaskForge account can keep any number of active Minecraft profiles. Each exact Minecraft UUID can belong to only one TaskForge account and cannot be linked twice. All profiles on the same TaskForge account share one Minecraft balance based on the account `UserId`; link and unlink operations never reset or restore the rating ledger. Individual profiles can be removed without affecting the other profiles or the shared balance.

## Minecraft plugin hot reload

After editing the plugin configs, a full Minecraft restart is not required:

- `TaskForgeLink`: `/tflink reload` (or `/taskforgelink refresh`). It reloads keys, URLs, HTTP listener, chat polling, the event-driven link cache, exemptions, debug options and the death-recovery manager.
- `CustomMobTweaks`: `/cmt reload` (or `/custommobtweaks refresh`). It unregisters the old listeners, cancels tracked Folia tasks, rereads the file and reconstructs every module.

Both commands are OP-only by default. Check the current state with `/tflink status` and `/cmt modules`.

## N-node high availability

TaskForge has an optional production cluster based on WireGuard, etcd, Patroni,
asynchronous PostgreSQL replication, N-way MinIO replication and Cloudflare
readiness routing. A/B/C are the initial full nodes; future D/E nodes are added from
the same `cluster.json` without hardcoding a triangle. Different host ports are
supported per node. See `deploy/cluster/QUICK_START_RU.md`.



## Cluster manager v32

See `TASKFORGE_102_CLUSTER_MANAGER_V32.md` for the latest A/B adoption and diagnostics fixes.

### WireGuard diagnostics

`bash ./cluster.sh status` and `bash ./cluster.sh doctor` automatically elevate only the cluster diagnostic process when needed. Linux restricts WireGuard peer/handshake metadata to privileged netlink access; automatic elevation prevents the old false `WireGuard peers expected=N actual=0` result for normal users. No manual `sudo wg ...` command is required.
