# TaskForge — ПОЛНЫЙ исходный проект для изолированной ветки K3s/YugabyteDB

**Этот архив уже содержит весь изменённый исходный код.** Никаких `APPLY.sh`, `INSTALL_K3S_BRANCH.sh`, других ZIP или загрузок `develop` для его использования не требуется. Это отдельная экспериментальная ветка; **не накатывай её на работающий production**.

## Что именно готово

- Полные исходники TaskForge, фронтенд и серверные проекты, с преобразованиями Stage 1–7.
- K3s на одном Linux ПК / VM, один YugabyteDB YSQL universe (один master, один tserver, RF=1, постоянные PVC). **RF=1 не обеспечивает отказоустойчивость** и не создаёт три копии данных.
- Локальный профиль `core`: фронтенд, gateway, 6 API (`identity`, `education`, `content`, `tasks`, `solutions`, `execution`), Redis, RabbitMQ и MinIO.
- Отдельные пространства имён `taskforge-k3s-lab` и `taskforge-k3s-lab-db`; базы `taskforge_k3s_*`; секреты создаются локально; доступ к сайту через `127.0.0.1:18080`.
- Новый CI `.github/workflows/k3s-sandbox-ci.yml`: проверка Kubernetes и Docker-контекстов, .NET 10 build и тесты, все фронтенд-тесты, сборка всех 7 Docker-образов без публикации, EF-миграции на временной YugabyteDB и отдельные GHCR-образы `taskforge-k3s-lab` с SHA-тегами. **Никакого автоматического деплоя на VPS или в production.**
- В исходниках сохранён необязательный функционал других микросервисов, но в режиме `core` он **не развёрнут**. Компиляторные runner'ы и SQL-песочницы тоже не поднимаются.
- Старая CT SPA, выделенный CT quiz-api, LLM-агент и прежние `deploy/prod`, `deploy/dev`, `deploy/cluster`, `scripts/prod`, `develop-*` workflows удалены из **этого дерева**. Обычные тестовые и математические задания остались.

**Важно:** живую совместимость каждого EF Core migration с YugabyteDB и запуск сайта можно подтвердить только на реальном K3s. Локальные Node-тесты и статические проверки не заменяют этот запуск. Данные со старого PostgreSQL здесь НЕ импортированы.

## 1. Обновить существующую ветку `cluster-v2` (НЕ `develop`)

В `cluster-v2` сейчас есть старая копия архива, где GitHub Actions не был подписан на имя ветки, а старые workflows остались после загрузки поверх файлов. **Обычное копирование новых файлов без удаления старых недостаточно.**

1. Распакуй данный ZIP в **отдельный каталог**, содержащий `.github`, `apps`, `services`, `tools` и т. д.
2. Сделай **свежий checkout только ветки `cluster-v2`** в новую папку:

```bash
git clone --branch cluster-v2 --single-branch https://github.com/PiVasya/taskforge.git taskforge-cluster-v2-new
cd taskforge-cluster-v2-new
test "$(git branch --show-current)" = cluster-v2 || exit 1
test -z "$(git status --porcelain)" || { echo 'Checkout is not clean'; exit 1; }

# УКАЖИ ПУТЬ К РАСПАКОВАННОМУ НОВОМУ ZIP (каталог с README_K3S_BRANCH.md)
SRC=/absolute/path/to/taskforge-cluster-v2-k3s-fixed
test -f "$SRC/.github/workflows/k3s-sandbox-ci.yml" || exit 1
# Удалить только старые файлы В ЭТОМ НОВОМ CHECKOUT, сохранив .git.
rsync -a --delete --exclude='.git/' "$SRC/" ./

# Проверка: остался только workflow K3s, старый production отсутствует.
ls .github/workflows/
test ! -f .github/workflows/develop-build.yml || exit 1
test ! -f .github/workflows/minecraft-plugins-build.yml || exit 1
python3 scripts/ci/check-k3s-workflow-trigger.py

git add -A
git diff --cached --stat
git commit -m 'Fix cluster-v2 K3s YugabyteDB CI and remove legacy stack'
git push origin cluster-v2
```

Не выполняй `rsync --delete` в каталоге работающего сервера или в рабочем `develop`. Эта команда предназначена **исключительно** для свежего checkout `cluster-v2`.

После push GitHub Actions должен запустить `K3s sandbox / YugabyteDB CI + isolated images`. Запускается сам CI, **не** развёртывание на домашнем ПК. Если какой-либо job упадёт, его лог покажет ошибку; публикация образов будет заблокирована.

**Если ты хочешь ветку с другим именем:** автоматический trigger допускает `k3s-*` и `k3s/**`. Ветки `main` и `develop` сознательно исключены.

## 2. Что делает новый CI/CD

При push ветки `cluster-v2` или `k3s-*`/`k3s/**` выполняется `.github/workflows/k3s-sandbox-ci.yml`:

1. Проверяет изоляцию манифестов, Docker-контексты и скрипты K3s.
2. Собирает шесть API на .NET 10, запускает их тесты и **все** фронтенд-тесты; собирает React.
3. Отдельный job собирает **семь настоящих Docker-образов без публикации**, проверяя ровно те build context, которые используются локально.
4. Поднимает **временную** YugabyteDB YSQL RF1 в GitHub Actions, создаёт отдельные базы `taskforge_k3s_*`, пытается применить все шесть наборов EF-миграций и проверяет таблицы и запись/чтение.
5. Только после успешных проверок публикует семь образов в отдельном GHCR-каталоге `ghcr.io/<owner>/taskforge-k3s-lab/<image>:<SHA>`.

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

Смотри `QA_BEFORE_PUSH_RU.md`. Автоматические локальные тесты и проверка конфигурации пройдены, но в среде аудита нет SDK .NET, Docker и K3s. Поэтому реальная компиляция C# и совместимость DDL с YugabyteDB не могут быть гарантированы до выполнения GitHub Actions. **Если `yugabyte-migrations` красный, публикация образов не начнётся**: нельзя считать такой push работоспособным развёртыванием.
