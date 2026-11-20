# TaskForge — ПОЛНЫЙ исходный проект для изолированной ветки K3s/YugabyteDB

**Этот архив уже содержит весь изменённый исходный код.** Никаких `APPLY.sh`, `INSTALL_K3S_BRANCH.sh`, других ZIP или загрузок `develop` для его использования не требуется. Это отдельная экспериментальная ветка; **не накатывай её на работающий production**.

## Что именно готово

- Полные исходники TaskForge, фронтенд и серверные проекты, для отдельной ветки K3s.
- K3s на одном Linux ПК / VM, один YugabyteDB YSQL universe (один master, один tserver, RF=1, постоянные PVC). **RF=1 не обеспечивает отказоустойчивость** и не создаёт три копии данных.
- Локальный профиль `core`: фронтенд, gateway, 6 API (`identity`, `education`, `content`, `tasks`, `solutions`, `execution`), `rating-worker`, Redis, RabbitMQ и MinIO.
- Отдельные пространства имён `taskforge-k3s-lab` и `taskforge-k3s-lab-db`; базы `taskforge_k3s_*`; секреты создаются локально; доступ к сайту через `127.0.0.1:18080`.
- Новый CI `.github/workflows/k3s-sandbox-ci.yml`: проверка Kubernetes и Docker-контекстов, .NET 10 build и тесты, все фронтенд-тесты, сборка всех 7 Docker-образов без публикации, EF-миграции на временной YugabyteDB и отдельные GHCR-образы `taskforge-k3s-lab` с SHA-тегами. **Никакого автоматического деплоя на VPS или в production.**
- В исходниках сохранён необязательный функционал других микросервисов, но в режиме `core` он **не развёрнут**. Компиляторные runner'ы и SQL-песочницы тоже не поднимаются.
- Старая CT SPA, выделенный CT quiz-api, LLM-агент и прежние `deploy/prod`, `deploy/dev`, `deploy/cluster`, `scripts/prod`, `develop-*` workflows удалены из **этого дерева**. Обычные тестовые и математические задания остались.

**Важно:** живую совместимость каждого EF Core migration с YugabyteDB и запуск сайта можно подтвердить только на реальном K3s. Локальные Node-тесты и статические проверки не заменяют этот запуск. Данные со старого PostgreSQL здесь НЕ импортированы.

Тесты ветки: `bash scripts/tests/repository.sh` (портативные проверки),
`bash scripts/tests/fast.sh` (.NET/Go и модели React),
`bash scripts/tests/all.sh` (включая сборку React и временную YugabyteDB).
Архитектура и подготовка новой БД: `docs/architecture/TASK_DATA_OWNERSHIP.md`.
На ПК до первого запуска сгенерируй две EF-миграции (Tasks и Solutions): `bash scripts/generate-migrations.sh ConsolidateAttempts tasks,solutions`. Генератор только создаёт исходные файлы EF-миграций: он не применяет SQL к БД.
Затем проверь созданные файлы и запусти `dotnet test` для Tasks, Solutions и
rating-worker. Проверку на реальной YugabyteDB выполнить отдельно перед использованием.

Миграция Education **не нужна**: экспериментальная staging-миграция удалена
вместе с прежней несостоявшейся архитектурой. Это допустимо только потому,
что отдельная K3s-БД ещё не инициализирована.

## 1. Состав архива и обновление исходников

В ZIP находится полный исходный проект для ветки `cluster-v2`. При копировании поверх существующего каталога Git старые файлы, отсутствующие в ZIP, **не удаляются автоматически**. Удаляй устаревшие отслеживаемые файлы из репозитория отдельно. Работающий production и ветку `develop` не затрагивать.

Процесс сборки, тестов и диагностики описан в [`docs/operations/K3S_CI_RUNBOOK.md`](K3S_CI_RUNBOOK.md).

## 2. Что делает новый CI/CD

При push ветки `cluster-v2` или `k3s-*`/`k3s/**` выполняется `.github/workflows/k3s-sandbox-ci.yml`:

1. Проверяет изоляцию манифестов, Docker-контексты и скрипты K3s.
2. Собирает шесть API на .NET 10, запускает их тесты и **все** фронтенд-тесты; собирает React.
3. Отдельный job собирает **восемь настоящих Docker-образов без публикации**, проверяя ровно те build context, которые используются локально.
4. Поднимает **временную** YugabyteDB YSQL RF1 в GitHub Actions, создаёт отдельные базы `taskforge_k3s_*`, пытается применить все шесть наборов EF-миграций и проверяет таблицы и запись/чтение.
5. Только после успешных проверок публикует восемь образов в отдельном GHCR-каталоге `ghcr.io/<owner>/taskforge-k3s-lab/<image>:<SHA>`.

CI не использует работающий PostgreSQL, старые GHCR-теги `:develop`/`:latest`, SSH к VPS или production secrets. GitHub Actions не умеет сам развёртывать на домашний ПК без отдельного self-hosted runner — поэтому для первого локального запуска используются следующие команды.

## 3. Подготовить Linux ПК

Нужен Linux x86_64 с systemd или отдельная Linux VM, ориентировочно от 12 ГБ RAM и 40 ГБ свободного SSD, Docker Engine, K3s, Helm, Node.js 22+, `kubectl`, `python3`, `openssl`. Для Windows используй отдельную Linux VM: сам K3s работает в Linux.

```bash
curl -sfL https://get.k3s.io | sh -
sudo k3s kubectl get nodes
```

Создай **отдельный** kubeconfig (не перезаписывай `~/.kube/config`):

```bash
mkdir -p "$HOME/.kube"
sudo install -m 0600 -o "$(id -u)" -g "$(id -g)" \
  /etc/rancher/k3s/k3s.yaml "$HOME/.kube/taskforge-k3s-lab.yaml"
export KUBECONFIG="$HOME/.kube/taskforge-k3s-lab.yaml"
export TASKFORGE_LOCAL_K3S_ACK=local-development-only
kubectl get nodes
```

В локальных скриптах есть fail-closed защита: кластер должен быть **одним Ready K3s-узлом** с Kubernetes API на `localhost`/`127.0.0.1`; на удалённый кластер эти скрипты применять запрещено.

## 4. Запуск из корня этого проекта

```bash
# Собирает образы ИЗ ЭТОГО исходного кода и загружает в containerd K3s.
bash infrastructure/k8s/taskforge-yugabyte/local/build-images.sh "$PWD" core

# Поднимает YugabyteDB RF1, создает отдельные БД/секреты/хранилища,
# выполняет одноразовые EF-migration Jobs, запускает приложения.
bash infrastructure/k8s/taskforge-yugabyte/local/run-local.sh "$PWD" core

# Отдельный терминал (тот же KUBECONFIG и TASKFORGE_LOCAL_K3S_ACK):
bash infrastructure/k8s/taskforge-yugabyte/local/port-forward.sh
```

Открой **http://127.0.0.1:18080**. В режиме `core` часть разделов сайта может показывать ошибки из-за неразвёрнутых optional-сервисов и runner'ов — это пока не весь функциональный production.

Все новые пароли генерируются локально в `~/.config/taskforge-k3s-lab/credentials.env` с правами `600`; файлы с секретами **не** включены в архив. Состояние YugabyteDB/MinIO/Redis/RabbitMQ хранится на отдельных PVC. Не удаляй их при повторном запуске, если нужны данные.

## 5. Диагностика

```bash
kubectl -n taskforge-k3s-lab get pods,jobs,pvc
kubectl -n taskforge-k3s-lab-db get pods,pvc
kubectl -n taskforge-k3s-lab logs job/migrate-tasks-api-lab1 --tail=200
curl -i http://127.0.0.1:18080/health/gateway
```

Если `migration Job` завершился ошибкой — смотри его логи, исправляй несовместимый SQL/EF именно в новой ветке, пересобирай образы и повторяй локальный запуск. Не переключай приложение на старый PostgreSQL.

Подробности реализации: `infrastructure/k8s/taskforge-yugabyte/local/START_LOCAL_RU.md`.

## 6. Предпушевый аудит и известное ограничение

Автоматические локальные тесты и проверки конфигурации не заменяют интеграционную проверку на реальном Kubernetes и YugabyteDB. Поэтому реальная компиляция C# и совместимость DDL с YugabyteDB не могут быть гарантированы до выполнения GitHub Actions. **Если `yugabyte-migrations` красный, публикация образов не начнётся**: нельзя считать такой push работоспособным развёртыванием.

