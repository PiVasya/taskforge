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
- `TASKFORGE_EXTERNAL_AI_MODEL=qwen3.6-plus`

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
TASKFORGE_EXTERNAL_AI_MODEL=qwen3.6-plus
TASKFORGE_EXTERNAL_AI_JSON_MODE=true
TASKFORGE_EXTERNAL_AI_TEMPERATURE=0.05
TASKFORGE_EXTERNAL_AI_QWEN_DISABLE_THINKING=true
TASKFORGE_EXTERNAL_AI_QWEN_ENABLE_SEARCH=false
```

Для TaskForge это важные настройки: `qwen3.6-plus` по умолчанию включает thinking-mode,
а для stage-based JSON pipeline это обычно только увеличивает задержку и чаще ломает формат.
Воркер теперь сам передаёт `enable_thinking=false` для Qwen по умолчанию, если вы это не отключили env-переменной.

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

## Оптимизация под Qwen3.6 Plus

В этой версии worker дополнительно оптимизирован под Qwen / DashScope:

- добавляет `system`-message с жёсткой инструкцией возвращать только JSON в JSON-стадиях;
- автоматически отключает `enable_thinking` для Qwen, чтобы уменьшить latency и повысить JSON discipline;
- автоматически отключает `enable_search`, чтобы ответы не тащили внешний веб-поиск в генерационный pipeline;
- поддерживает `TASKFORGE_EXTERNAL_AI_QWEN_THINKING_BUDGET`, если всё же нужен controlled thinking;
- поддерживает `TASKFORGE_EXTERNAL_AI_SEED`, `TASKFORGE_EXTERNAL_AI_TOP_P`, `TASKFORGE_EXTERNAL_AI_TOP_K` для более стабильной генерации.


## Режим quality-first для Qwen3.6

В этой сборке worker дополнительно заточен под медленный, аккуратный режим:

- ставит паузу между внешними запросами, чтобы не долбить free-route слишком часто;
- при `429 rate limit` автоматически ждёт дольше и наращивает cooldown;
- хранит краткоживущую память в пределах одного вызова: если первый ответ модели не распарсился как JSON, следующая попытка получает предыдущий ответ как контекст и просит исправить его;
- по умолчанию использует более низкую температуру и больше попыток, чтобы повышать стабильность вместо скорости.

Рекомендуемые env для максимального качества на OpenRouter free route:

```env
TASKFORGE_EXTERNAL_AI_PROVIDER=openai_compatible
TASKFORGE_EXTERNAL_AI_BASE_URL=https://openrouter.ai/api/v1
TASKFORGE_EXTERNAL_AI_MODEL=qwen/qwen3.6-plus:free
TASKFORGE_EXTERNAL_AI_TEMPERATURE=0.03
TASKFORGE_EXTERNAL_AI_REQUEST_ATTEMPTS=4
TASKFORGE_EXTERNAL_AI_RETRY_BACKOFF_SECONDS=20
TASKFORGE_EXTERNAL_AI_QWEN_DISABLE_THINKING=false
TASKFORGE_EXTERNAL_AI_QWEN_ENABLE_SEARCH=false
TASKFORGE_EXTERNAL_AI_QWEN_THINKING_BUDGET=8192
TASKFORGE_EXTERNAL_AI_MIN_REQUEST_INTERVAL_SECONDS=18
TASKFORGE_EXTERNAL_AI_POST_SUCCESS_COOLDOWN_SECONDS=12
TASKFORGE_EXTERNAL_AI_RATE_LIMIT_COOLDOWN_SECONDS=60
TASKFORGE_EXTERNAL_AI_MAX_RATE_LIMIT_COOLDOWN_SECONDS=240
TASKFORGE_EXTERNAL_AI_RETRY_WITH_RESPONSE_MEMORY=true
TASKFORGE_EXTERNAL_AI_RESPONSE_MEMORY_MAX_CHARS=5000
POLL_INTERVAL_SECONDS=20
```
