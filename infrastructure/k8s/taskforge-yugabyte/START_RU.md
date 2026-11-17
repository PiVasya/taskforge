# TaskForge: что изменилось в Stage 5

Этот архив — **накопительный исходный патч**, не готовая установленная K3s-система и не архив всех файлов репозитория. Для применения нужен **чистый `PiVasya/taskforge`** на коммите `8c1ddc0956c6f32e8803d4dc97352d4eae71b529`.

```bash
bash APPLY.sh /path/to/clean/taskforge
```

Никаких действий с GitHub или рабочими серверами APPLY.sh не делает. Stage 1–4 включены в этот архив: отдельно патч Stage 4 применять не нужно.

## Новая БД

- Для новой K3s-версии выбран **YugabyteDB YSQL** — не PostgreSQL storage, хотя .NET продолжает подключаться через PostgreSQL-протокол Npgsql (`5433`).
- При `RF=3` подтверждённые записи в реплицированных фрагментах хранятся на трёх *data nodes*. Запись подтверждается большинством реплик (2/3); API выполняет **одну транзакцию**, а не три независимых записи.
- Три раздельные установки YugabyteDB — не единая база. Для трёх стран нужен **один** YugabyteDB universe с тремя дата-узлами, межкластерным DNS/маршрутизацией/TLS. Шаблон `geo-universe.template.yaml` **нельзя применять без конфигурации оператора**.
- Существующие логические базы сервисов сохраняются. Путаница `TaskAttempts` математики/тестов ещё не исправлена; эта схема потребует отдельного переноса с изменением API.
- Отдельные `sql-postgres`/`sql-mysql` служат для **безопасного выполнения SQL пользователей**, а не для данных самого TaskForge. Они не должны быть доступны из приложенческого YugabyteDB.

## Что можно проверить на тестовом кластере

Для настоящего RF3 нужен K3s с **тремя независимыми физическими узлами**. Пометить три узла:

```bash
kubectl label node NODE_A taskforge.io/db-node=true
kubectl label node NODE_B taskforge.io/db-node=true
kubectl label node NODE_C taskforge.io/db-node=true
TASKFORGE_YUGABYTE_LAB_ACK=yes-lab-only bash infrastructure/k8s/taskforge-yugabyte/db/install-lab.sh
TASKFORGE_YUGABYTE_LAB_ACK=yes-lab-only bash infrastructure/k8s/taskforge-yugabyte/db/create-lab-databases.sh
```

**Это лабораторный запуск, без production TLS/credentials/storage.** Старую PostgreSQL БД эти скрипты не трогают. Физически проверить отказоустойчивость можно только на развёрнутом трёхузловом YugabyteDB стенде.

В полный исходный checkout добавлены `tools/k3s/generate.mjs`, `tools/k3s/preflight.sh` и `tools/k3s/check-ysql-compat.mjs`. Генератор даёт **13 migration Jobs** (12 сервисов + Telegram quiz bot), **12 API deployments**, **12 API services**, два **worker deployments** (rating, Telegram quiz). Везде потребуются подготовленные Kubernetes Secrets с отдельными подключениями YSQL.

Подробный порядок: `infrastructure/k8s/taskforge-yugabyte/README.md`.

## Что остаётся недоделанным

Реальный multi-country YugabyteDB universe, K3s gateway/frontend, раннеры и SQL-изоляция, Redis, RabbitMQ, MinIO, согласованный перенос существующих данных с PostgreSQL, production TLS, бэкапы, chaos/integration tests. Это **не production-ready релиз**. Старый production PostgreSQL не удаляем и не переписываем.
