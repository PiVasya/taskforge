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
