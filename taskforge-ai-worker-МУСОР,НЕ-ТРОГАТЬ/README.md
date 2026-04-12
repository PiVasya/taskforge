# taskforge-ai-worker

Отдельный worker-контейнер для TaskForge AI.

Он:
- забирает jobs из `api/internal/ai/jobs/pull`
- по возможности отправляет prompt в Ollama
- возвращает `ResultJson` обратно в TaskForge

Worker не обязан работать постоянно. Если он выключен, jobs просто остаются в статусе `pending`.

## Self-check pipeline

Worker теперь умеет не только генерировать JSON-draft, но и выполнять базовый Python self-check:

- `code-test`: прогоняет `referenceSolutionPython` по `publicTests` и `hiddenTests`;
- `math`: проверяет валидность структуры блоков и числовых accepted answers;
- `test`: проверяет структуру вопросов и корректных ответов.

Результат кладётся в `draft.meta.selfCheck`.


## Deployment

### 1) Build image in GitHub Actions

После добавления workflow `build-ai-worker.yml` GitHub будет собирать и пушить образ:

- `ghcr.io/<owner>/taskforge-ai-worker:latest`

### 2) Deploy on a separate AI machine

Если worker и Ollama крутятся на отдельном устройстве, удобнее запускать не весь TaskForge, а только worker-stack:

```bash
docker compose -f docker-compose.ai-worker.yaml up -d
```

Нужные env-переменные:

- `TASKFORGE_API_BASE=https://<your-taskforge-host>`
- `TASKFORGE_INTERNAL_KEY=<same API_INTERNAL_KEY as on backend>`
- `OLLAMA_BASE_URL=http://ollama:11434` или URL внешнего Ollama
- `OLLAMA_MODEL=qwen3:14b`

Если Ollama на той же AI-машине и тоже нужен контейнером, запускай с профилем:

```bash
docker compose -f docker-compose.ai-worker.yaml --profile with-ollama up -d
```


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
