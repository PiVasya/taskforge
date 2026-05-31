# Recheck report

Дата проверки: 2026-05-29

## Что изменено в этом апдейте

1. Dev и prod теперь лежат рядом в `deploy/`:
   - `deploy/dev/`
   - `deploy/prod/`
2. У dev и prod одинаковая понятная структура:
   - `.env.example`
   - `README.md`
   - `compose.sh`
   - `compose/`
3. Старые расположения удалены:
   - `infrastructure/compose/dev/`
   - `production/`
   - `scripts/dev-compose.sh`
4. Workflow обновлён на новые пути `deploy/**`.
5. Root `compose.yaml` по-прежнему отсутствует, чтобы не возвращать один огромный compose-файл.

## Проверено

- YAML syntax для всех split compose-файлов.
- Dev compose build Dockerfile paths.
- Prod compose использует только `image:`, без `build:`.
- Нет root `compose.yaml` и нет монолитного prod compose.
- Python вне `services/analyzers/image-analyzer` отсутствует.
- EF `Migrations/` директории отсутствуют.
- `MIGRATIONS_REQUIRED.md` присутствуют у DB-owning сервисов.
- `extracted/` исключён из компиляции микросервисов.
- `.csproj` XML синтаксис валиден.
- Go runner tests проходят.
- Имена production images совпадают с CI matrix.

Полный лог: `docs/verification/LAST_VERIFY_LOG.txt`.

## Hotfix после реального dev build

Пользовательский запуск `./deploy/dev/compose.sh up --build` дошёл до сборки большинства сервисов, но упал на `image-pascal-runner` при распаковке `PABCNETC.zip`.

Причина: `unzip` в Debian некорректно обработал архив PascalABC.NET с кириллическими именами файлов и выдал ошибку вида `mismatching "local" filename`. Из-за этого `pabcnetc.exe` не находился, и Docker build завершался с `exit code: 1`.

Исправление:

- в `services/execution/runners/image-pascal-runner/Dockerfile` добавлен `p7zip-full`;
- распаковка PascalABC.NET теперь сначала выполняется через `7z`, а `unzip` оставлен только как fallback;
- добавлена диагностическая ошибка, которая печатает список найденных файлов, если `pabcnetc.exe` всё равно не найден.

После правки повторно проверено:

- YAML syntax: ok;
- Go runner tests: ok;
- Python вне `services/analyzers/image-analyzer`: отсутствует;
- EF `Migrations/` директории: отсутствуют.

Docker build в этой среде всё ещё не запускался, потому что Docker CLI/daemon здесь недоступен. Исправление сделано по реальному логу сборки пользователя.

## 2026-05-31 startup-order fix

Real dev run result supplied by user:

- All Docker images built successfully, including `taskforge-dev-image-pascal-runner`.
- `image-pascal-runner` started and listened on `:8000`.
- Multiple DB-owning APIs failed with `NpgsqlException: Connection refused` because they attempted EF migrations while PostgreSQL was still bootstrapping.
- Gateway failed to bind `0.0.0.0:8080` because port 8080 was already in use on the host.

Applied changes:

- `postgres` healthcheck extended to `start_period: 20s`, `interval: 5s`, `retries: 60`.
- `rabbitmq` healthcheck extended similarly.
- Every service depending on `postgres` now uses `depends_on: postgres: condition: service_healthy`.
- Every service depending on `rabbitmq` now uses `depends_on: rabbitmq: condition: service_healthy`.
- Dev gateway port is now configurable with `DEV_GATEWAY_HTTP_PORT` and defaults to `18080`.
- Dev storage ports are configurable with `DEV_POSTGRES_PORT`, `DEV_RABBITMQ_PORT`, `DEV_RABBITMQ_MANAGEMENT_PORT`, `DEV_MINIO_PORT`, `DEV_MINIO_CONSOLE_PORT`.

No EF migrations were generated.
