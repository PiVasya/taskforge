# taskforge-ai-worker-external

Отдельный AI worker для TaskForge, который **не использует локальный Ollama**.

Он сохраняет тот же pipeline и тот же контракт с backend API, что и обычный
`taskforge-ai-worker`, но вместо локальной модели ходит во **внешний API**.

Старый локальный worker **оставлен без изменений** в папке `taskforge-ai-worker`.
Эта папка — новый альтернативный вариант под внешний AI.

## Что поддерживается

По умолчанию worker работает через **OpenAI-compatible Chat Completions API**.
Это удобно для:

- OpenAI
- DeepSeek API
- OpenRouter
- DashScope / Qwen compatible-mode
- других совместимых провайдеров

Также есть отдельный режим `anthropic`.

## Как запускать

```bash
docker compose -f docker-compose.external-ai-worker.yaml up -d --build
```

## Основные env-переменные

- `TASKFORGE_API_BASE=https://<your-taskforge-host>`
- `TASKFORGE_INTERNAL_KEY=<тот же API_INTERNAL_KEY, что и у backend>`
- `TASKFORGE_AI_WORKER_ID=taskforge-ai-worker-external`
- `TASKFORGE_EXTERNAL_AI_PROVIDER=openai_compatible`
- `TASKFORGE_EXTERNAL_AI_BASE_URL=https://api.openai.com/v1`
- `TASKFORGE_EXTERNAL_AI_API_KEY=<secret>`
- `TASKFORGE_EXTERNAL_AI_MODEL=gpt-4.1-mini`

Дополнительно:

- `TASKFORGE_EXTERNAL_AI_REQUEST_ATTEMPTS=2`
- `TASKFORGE_EXTERNAL_AI_RETRY_BACKOFF_SECONDS=8`
- `TASKFORGE_EXTERNAL_AI_JSON_MODE=true`
- `TASKFORGE_EXTERNAL_AI_TEMPERATURE=0.12`
- `TASKFORGE_EXTERNAL_AI_EXTRA_HEADERS=` — JSON-объект или список `Header: value`

## Примеры провайдеров

### OpenAI

```env
TASKFORGE_EXTERNAL_AI_PROVIDER=openai_compatible
TASKFORGE_EXTERNAL_AI_BASE_URL=https://api.openai.com/v1
TASKFORGE_EXTERNAL_AI_MODEL=gpt-4.1-mini
```

### DeepSeek

```env
TASKFORGE_EXTERNAL_AI_PROVIDER=openai_compatible
TASKFORGE_EXTERNAL_AI_BASE_URL=https://api.deepseek.com/v1
TASKFORGE_EXTERNAL_AI_MODEL=deepseek-chat
```

### OpenRouter

```env
TASKFORGE_EXTERNAL_AI_PROVIDER=openai_compatible
TASKFORGE_EXTERNAL_AI_BASE_URL=https://openrouter.ai/api/v1
TASKFORGE_EXTERNAL_AI_MODEL=openai/gpt-4.1-mini
TASKFORGE_EXTERNAL_AI_EXTRA_HEADERS={"HTTP-Referer":"https://your-site.example","X-Title":"TaskForge"}
```

### Qwen / DashScope compatible mode

```env
TASKFORGE_EXTERNAL_AI_PROVIDER=openai_compatible
TASKFORGE_EXTERNAL_AI_BASE_URL=https://dashscope-intl.aliyuncs.com/compatible-mode/v1
TASKFORGE_EXTERNAL_AI_MODEL=qwen-plus
```

### Anthropic

```env
TASKFORGE_EXTERNAL_AI_PROVIDER=anthropic
TASKFORGE_EXTERNAL_AI_BASE_URL=https://api.anthropic.com/v1
TASKFORGE_EXTERNAL_AI_MODEL=claude-sonnet-4-20250514
```

## Замечания

- worker по-прежнему возвращает в backend `modelName`, `resultJson` и т.д.;
- старый pipeline сохранён почти полностью, чтобы можно было сравнивать локальный и внешний AI;
- этот вариант нужен именно для следующего шага: тестировать внешний AI без удаления старого локального worker-а.

## Что добавлено в больших апдейтах

- OpenRouter-native structured outputs + schema validation по ключевым стадиям.
- Route-aware repair и substage draft generation.
- Stage-aware provider routing profiles (`planning/chat/draft/repair/review`).
- Selection telemetry и signature-based duplicate hints.
- Sandbox runner в двух режимах: `process` и `container`.

### Дополнительные env

- `TASKFORGE_AI_PROVIDER_ORDER_PLANNING=anthropic,openai`
- `TASKFORGE_AI_PROVIDER_ORDER_CHAT=openai,anthropic`
- `TASKFORGE_AI_PROVIDER_ORDER_DRAFT=openai,anthropic`
- `TASKFORGE_AI_PROVIDER_ORDER_REPAIR=openai,anthropic`
- `TASKFORGE_AI_STAGE_ROUTING_JSON={...}` — точечные overrides по stage/group
- `TASKFORGE_AI_DUPLICATE_SIGNATURES=true`
- `TASKFORGE_AI_SIMILARITY_SIGNATURE_WARNING=0.66`
- `TASKFORGE_AI_SIMILARITY_SIGNATURE_FAIL=0.82`
- `TASKFORGE_AI_SANDBOX_MODE=process|container`
- `TASKFORGE_AI_RUNNER_RUNTIME=docker`
- `TASKFORGE_AI_RUNNER_CONTAINER_IMAGE=taskforge-python-runner:wave4`

### Containerized sandbox

Для более жёсткой изоляции можно собрать runner image отдельно:

```bash
docker build -f taskforge-ai-worker-external/runner.Dockerfile -t taskforge-python-runner:wave4 .
```

После этого включить:

```env
TASKFORGE_AI_SANDBOX_RUNNER=true
TASKFORGE_AI_SANDBOX_MODE=container
TASKFORGE_AI_RUNNER_CONTAINER_IMAGE=taskforge-python-runner:wave4
```

Такой режим запускает решения через `docker run --network none --read-only ...` и оставляет fallback на обычный process-mode, если runtime недоступен.


## Логи worker

Worker пишет логи одновременно:
- в stdout/stderr контейнера
- в текстовый файл `TASKFORGE_AI_LOG_FILE` (по умолчанию `/app/logs/worker.log`)
- в JSONL-файл `TASKFORGE_AI_JSON_LOG_FILE` (по умолчанию `/app/logs/worker.jsonl`)

Рекомендуемый mount:

```yaml
volumes:
  - ./logs/taskforge-ai-worker-external:/app/logs
```

Тогда после запуска можно забирать:

```bash
cat ./logs/taskforge-ai-worker-external/worker.log
cat ./logs/taskforge-ai-worker-external/worker.jsonl
```


## Wave5 additions
- Canonical external-LLM adapter import path is now `llm_client.py`; legacy `ollama.py` stays as a compatibility shim.
- LLM success logs now include token/cost telemetry when the provider returns usage metadata.
- Duplicate analysis now includes signature-based clustering for reference assignments and peer drafts.
- GitHub Actions includes `test-external-ai-worker.yml` to run compileall + pytest before shipping worker-only changes.


## Wave6 telemetry and duplicate tuning

- `TASKFORGE_AI_WORKER_TELEMETRY=true` — worker sends `telemetryJson` to backend complete endpoint.
- `TASKFORGE_AI_DUPLICATE_CLUSTER_WARNING_SIZE=2` — cluster size that triggers warning.
- `TASKFORGE_AI_DUPLICATE_CLUSTER_FAIL_SIZE=3` — cluster size that triggers fail.


## Preflight перед ручным тестом

Перед запуском worker теперь можно быстро проверить конфиг:

```bash
cd taskforge-ai-worker-external
python preflight.py
```

Для контейнерного healthcheck используется тот же скрипт:

```bash
python preflight.py --healthcheck
```

Если включён container sandbox, preflight проверит наличие `docker` CLI и базовую пригодность окружения.

## Что важно перед тестом проекта целиком

После добавления `TaskAssignment.IsAiGenerated` не забудь:

1. Сгенерировать миграцию вручную.
2. Применить её к базе.
3. Только потом тестировать публикацию AI-draft и чтение заданий.

Краткий чек-лист лежит в корне репозитория: `TESTING_NOW.md`.
