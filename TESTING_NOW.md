# Что проверить прямо сейчас

Это короткий чек-лист перед ручным тестом проекта после последних апдейтов.

## 1. Сгенерировать миграцию под `IsAiGenerated`

Из корня репозитория:

```bash
dotnet ef migrations add AddIsAiGeneratedToTaskAssignments \
  --project taskforge/taskforge.csproj \
  --startup-project taskforge/taskforge.csproj \
  --context ApplicationDbContext \
  --output-dir Migrations
```

Применить:

```bash
dotnet ef database update \
  --project taskforge/taskforge.csproj \
  --startup-project taskforge/taskforge.csproj \
  --context ApplicationDbContext
```

## 2. Проверить внешний worker до запуска

```bash
cd taskforge-ai-worker-external
python preflight.py
```

Для краткого health-check:

```bash
python preflight.py --healthcheck
```

## 3. Если нужен container sandbox — сначала собрать runner image

Из корня репозитория:

```bash
docker build -f taskforge-ai-worker-external/runner.Dockerfile -t taskforge-python-runner:wave4 .
```

И включить env:

```env
TASKFORGE_AI_SANDBOX_RUNNER=true
TASKFORGE_AI_SANDBOX_MODE=container
TASKFORGE_AI_RUNNER_CONTAINER_IMAGE=taskforge-python-runner:wave4
```

## 4. Запустить внешний worker

```bash
docker compose -f docker-compose.external-ai-worker.yaml up -d --build
```

Проверить health:

```bash
docker compose -f docker-compose.external-ai-worker.yaml ps
docker compose -f docker-compose.external-ai-worker.yaml logs -f taskforge-ai-worker-external
```

## 5. Что руками проверить в приложении

### Проверка поля `IsAiGenerated`
- создать/опубликовать AI-draft;
- убедиться, что опубликованное задание получает `IsAiGenerated = true`;
- создать обычное ручное задание;
- убедиться, что у него `IsAiGenerated = false`.

### Проверка внешнего worker
- AI chat turn не должен выполнять destructive action без `confirmed=true`;
- worker должен логировать telemetry и usage/meta при ответах провайдера;
- при OpenRouter должен использовать structured outputs/schema validation;
- при similarity review не должно быть явного спама ложных дублей на коротких простых задачах.

### Проверка sandbox
- в process-mode решения проходят как раньше;
- в container-mode worker не падает, если image уже собран;
- таймауты и runtime errors различаются в логах/telemetry.

## 6. Минимальный rollback

Если перед ручным тестом нужно быстро упростить конфиг:

```env
TASKFORGE_AI_DRAFT_SUBSTAGES=false
TASKFORGE_AI_SANDBOX_RUNNER=false
TASKFORGE_AI_CHAT_STRICT_MODE=true
TASKFORGE_AI_STRUCTURED_OUTPUTS=true
TASKFORGE_AI_SCHEMA_VALIDATION=soft
```

Такой режим оставляет основной upgrade-контур, но делает поведение мягче для первого ручного прогона.
