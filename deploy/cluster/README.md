# TaskForge Cluster Manager v32

Пользовательская команда одна:

```bash
bash ./cluster.sh help
```

Внутри по-прежнему используются Docker, WireGuard, PostgreSQL streaming replication, MinIO bucket replication и, после появления третьего независимого узла, Patroni + etcd. Но вручную редактировать Compose, `pg_hba.conf`, replication slots и правила MinIO больше не требуется.

Host dependencies (`socat`, WireGuard tools, `jq`, `curl`, Docker и т.д.) менеджер устанавливает автоматически для mutating-команд. `bootstrap` остаётся явной командой для новой машины, но `adopt`/`apply`/`join` сами дотягивают недостающие пакеты.

## Два режима

### `replica` — сейчас, для A и B

- A — основной TaskForge и PostgreSQL primary.
- B — пассивный узел с асинхронной PostgreSQL standby.
- MinIO реплицируется во все стороны.
- RabbitMQ и Redis локальны на каждом узле; WAN-кластер из них не строится.
- Обычный PostgreSQL `COMMIT` ждёт только локальный диск primary и не ждёт удалённые узлы.
- Автоматический consensus-failover не изображается: двух независимых голосов недостаточно.

### `quorum` — позже, когда появится независимый C

- A/B/C становятся voters etcd.
- Patroni выбирает единственный writable PostgreSQL.
- A предпочтителен, затем B, затем C, но свежесть WAL важнее приоритета.
- Асинхронная репликация сохраняется.
- Возврат на A выполняется штатным controlled switchover.
- D/E могут быть полноценными TaskForge-репликами без расширения etcd-voters.

## Текущие A и B

Рекомендуемый путь v32 — **одна команда на узел**, без ручной установки `socat`, WireGuard tools, Docker, без копирования analyzer keys и без переписывания путей `.env`:

```bash
# A
bash ./cluster.sh migrate-local A --from /path/to/old-server-folder

# B
bash ./cluster.sh migrate-local B --from /path/to/old-server-folder
```

`migrate-local` автоматически ставит отсутствующие host dependencies, переносит `.env`/`config.json`/Code Analyzer keys, исправляет владельцев и пути, а затем вызывает безопасный `adopt`.

Если секреты уже находятся в новой папке, можно сразу выполнить:

```bash
bash ./cluster.sh adopt A
bash ./cluster.sh adopt B
```

`adopt` ничего не удаляет и не пересоздаёт. Он подхватывает уже работающие WireGuard/PostgreSQL/MinIO, создаёт локальную конфигурацию менеджера и проверяет роль.

## Чистая установка A/B

На A:

```bash
bash ./cluster.sh bootstrap
bash ./cluster.sh import-secrets ./taskforge-cluster-secrets.enc
bash ./cluster.sh init-primary A
```

На B:

```bash
bash ./cluster.sh bootstrap
bash ./cluster.sh import-secrets ./taskforge-cluster-secrets.enc
bash ./cluster.sh join B --yes
```

`join` автоматически:

1. применяет локальные порты;
2. поднимает WireGuard и UFW;
3. создаёт physical replication slot;
4. выполняет `pg_basebackup`;
5. проверяет `streaming / async`;
6. выбирает обычный MinIO или `-cpuv1`;
7. запускает локальные MinIO/RabbitMQ/Redis;
8. создаёт versioned bucket;
9. строит N-way MinIO replication;
10. оставляет приложения на standby выключенными.

## Добавление C или D

На новом сервере:

```bash
bash ./cluster.sh bootstrap
bash ./cluster.sh prepare-node \
  --node C \
  --public-ip 203.0.113.30 \
  --wg-ip 10.80.0.3 \
  --priority 80 \
  --quorum-voter
```

Если локальные порты заняты, добавляются параметры:

```bash
--http-port 8080 \
--https-port 8443 \
--postgres-port 55432 \
--minio-port 19000 \
--minio-console-port 19001
```

На A:

```bash
bash ./cluster.sh add-node taskforge-node-C.json
```

Полученный `taskforge-cluster-topology.json` копируется на каждый узел:

```bash
bash ./cluster.sh import-topology taskforge-cluster-topology.json
bash ./cluster.sh apply NODE_ID
```

После применения топологии на C:

```bash
bash ./cluster.sh join C --yes
```

## Почему разные порты не проблема

В inventory различаются локальные порты. Внутри WireGuard используются стабильные cluster ports:

```text
PostgreSQL: 10.80.0.x:5432
MinIO:     10.80.0.x:9000
```

Если на C PostgreSQL должен локально занимать `55432`, менеджер ставит systemd/socat proxy:

```text
10.80.0.3:5432 -> 127.0.0.1:55432
```

Compose-файлы одинаковы на всех узлах и вручную не редактируются.

## Диагностика и безопасный ремонт

```bash
bash ./cluster.sh status
bash ./cluster.sh doctor
bash ./cluster.sh repair
```

- `status` показывает роли, lag, WireGuard, MinIO, RabbitMQ/Redis.
- `doctor` ничего не изменяет.
- `repair` исправляет права, локальный профиль CPU, WireGuard, UFW и proxy, но никогда не удаляет volume.

## Переход к автоматическому failover

После появления трёх независимых voters:

```bash
bash ./cluster.sh quorum-prepare
```

на A/B/C. После того как etcd здоров на всех voters:

```bash
# A
bash ./cluster.sh quorum-enable-primary

# B/C/D...
bash ./cluster.sh quorum-join
```

Расширенный слой использует существующие безопасные сценарии миграции/rollback и отдельный `postgres-cluster-data`, поэтому старый standalone volume автоматически не удаляется.

## Что менеджер принципиально не делает

- не запускает `docker compose down -v`;
- не выполняет `docker volume prune`;
- не перезаписывает непустую PostgreSQL-копию без явного `--reset-data`;
- не публикует PostgreSQL и MinIO на `0.0.0.0`;
- не делает RabbitMQ-кластер через WAN;
- не хранит topology/порты в общей `.env`;
- не копирует private WireGuard keys между серверами.
