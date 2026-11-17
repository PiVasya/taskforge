# TaskForge K3s / YugabyteDB: аудит перед push

Это полный исходный код для отдельной ветки `k3s-local`. Его нельзя смешивать с `develop` или действующим Docker Compose production.

## Что исправлено после аудита исходного ZIP

- В `education-api` и `tasks-api` исправлены Dockerfile: общий `services/shared/Cluster/DistributedSignalR.cs` теперь попадает в SDK build stage.
- Добавлена CI-проверка Docker-контекстов и всех локальных `.csproj`-ссылок на общие C#-исходники.
- Удалён устаревший тест удалённого LLM-агента, который ссылался на уже отсутствующий `agentModel.js`. Анализатор аккаунтов не удалялся.
- Для шести миграционных API добавлена Npgsql EF Core реализация истории без PostgreSQL-специфичного `LOCK TABLE ... ACCESS EXCLUSIVE`, используемая **только** при `Database__RequireYugabyte=true` и `Database__MigrationJobOnly=true`. Традиционные API-процессы используют обычную реализацию Npgsql. Одновременно разрешён только один Job для каждой базы.
- Локальные EF миграции запускаются последовательно, а не параллельно. При ошибке не запускается API.
- Исправлен способ монтирования конфигурации Nginx: каталог ConfigMap целиком вместо ненадёжного `subPath` к отдельному файлу.
- CI независимо собирает все семь Docker-образов **без публикации**; не ждёт результата проверки миграций для обнаружения ошибок сборки Dockerfile.
- В CI добавлен полный набор Node-тестов фронтенда, архитектурная проверка React и проверка схемы **всех шести** баз YugabyteDB с тестом INSERT/SELECT.

## Что действительно проверено без сети

- `node --test tools/k3s/local/local.test.mjs tools/k3s/local/sandbox.test.mjs`
- `cd apps/web && node --test scripts/*/*.test.mjs`
- `cd apps/web && node scripts/check-architecture.js`
- `python3 scripts/ci/check-k3s-docker-context.py`
- `python3 scripts/ci/check-k3s-migration-contract.py`
- Синтаксис shell-скриптов, генерация Core/All-API Kubernetes ресурсов, парсинг CI YAML, синтаксис сгенерированного Nginx.

**Нет подтверждения реальной сборки .NET 10, Docker-образов, запуска K3s и применения EF-миграций к YSQL.** В среде проверки отсутствуют `dotnet`, `docker`, `helm` и `kubectl`. Даже если все локальные проверки зелёные, это не гарантия успешного запуска сайта.

**Риск YugabyteDB:** штатные EF/Npgsql миграции могут использовать транзакционные DDL, а поддержка транзакционной DDL в выбранной версии YugabyteDB ограничена. Отдельное переопределение history-lock не отменяет этот риск. Поэтому job `yugabyte-migrations` — обязательный блокирующий тест, а не декоративная проверка. При его сбое нужны точные CI-логи и правка схемы/мигратора.

## Как пушить и понимать результат

1. Публикуй **содержимое каталога** (не ZIP как единственный файл) в новую ветку `k3s-local`.
2. В Actions открой `K3s sandbox / YugabyteDB CI + isolated images`.
3. Должны пройти `k3s-manifests`, шесть `dotnet-build`, `frontend`, семь `docker-build-smoke`, `yugabyte-migrations`.
4. Только тогда `publish-lab-images` публикует SHA-образы, не касаясь production.
5. При ошибке скопируй лог **первого** упавшего job; особенно `yugabyte-migrations` и `docker-build-smoke`.

Все новые логические базы называются `taskforge_k3s_*`, namespaces `taskforge-k3s-lab` и `taskforge-k3s-lab-db`, интерфейс локальный `http://127.0.0.1:18080`. Запускающие скрипты требуют Kubeconfig на `127.0.0.1` и ровно один узел K3s. Текущая конфигурация YugabyteDB — RF1 для локального тестирования, а не три независимые копии данных.
