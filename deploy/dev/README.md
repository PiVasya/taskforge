# Dev environment

Dev-окружение лежит на одном уровне с prod:

```text
deploy/dev/
  .env.example
  compose.sh
  compose/
```

## Запуск

```bash
cp deploy/dev/.env.example deploy/dev/.env
./deploy/dev/compose.sh up --build
```

## Остановка

```bash
./deploy/dev/compose.sh down
```

## Особенности

- Использует `build:` и собирает сервисы из локального кода.
- Использует `ASPNETCORE_ENVIRONMENT=Development`.
- Gateway открыт на `http://localhost:18080` по умолчанию. Если нужен старый порт, поменяй `DEV_GATEWAY_HTTP_PORT=8080` в `deploy/dev/.env`, но только если порт свободен.
- Миграции лежат в репозитории как baseline `InitialMicroserviceSchema`; автоприменение включается через `MIGRATE_ON_STARTUP=true`.
## Если порт занят

Dev gateway по умолчанию использует порт `18080`, чтобы не конфликтовать с уже занятым `8080`. Изменить можно в `deploy/dev/.env`:

```text
DEV_GATEWAY_HTTP_PORT=18080
```

## Порядок старта

Compose ждёт `postgres` и `rabbitmq` через healthcheck перед стартом API/worker-сервисов. Это нужно, чтобы автоприменение EF migrations не падало с `Connection refused`, пока PostgreSQL ещё инициализируется.


## Startup logs

To avoid flooding the terminal, use:

```bash
./deploy/dev/compose.sh up-logs --build
```

It starts the stack in detached mode and saves the first 30 seconds of logs to `deploy/dev/logs/<timestamp>/startup-30s.log`.

Useful commands:

```bash
./deploy/dev/compose.sh logs -f --tail=200
./deploy/dev/compose.sh logs-dump 1000
./deploy/dev/compose.sh logs-startup 30
```
