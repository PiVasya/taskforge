# TaskForge AI scaffolding

Этот набросок добавляет в проект отдельный AI-слой, который живёт **поверх существующих типов заданий** (`test`, `math`, `code-test`, `image-test`) и не ломает текущую платформу.

## Что уже добавлено

- `AiJobs` — очередь AI-задач.
- `AiJobFiles` — ссылки на файлы, которые AI должен изучить.
- `AiGeneratedAssignmentDrafts` — черновики заданий, которые нейросеть сгенерировала, но человек ещё не утвердил.
- `AiSubmissionReviews` — AI-review решений пользователей.
- `AiUserRiskReports` — отчёты о подозрительном/аномальном поведении.
- `AiAssignmentInsights` — анализ качества уже существующих заданий.
- `Admin AI` страница на фронте.
- `Internal AI Jobs` API для отдельного worker-контейнера.
- `taskforge-ai-worker` — отдельный контейнер, который умеет забирать задания из очереди и стучаться в Ollama.

## Базовая идея

1. Основной backend кладёт задачу в `AiJobs`.
2. Отдельный worker забирает её через internal API.
3. Worker дергает Ollama или возвращает fallback-черновик.
4. Результат сохраняется в `ResultJson` и, если это генерация задания, автоматически складывается в `AiGeneratedAssignmentDrafts`.
5. Человек-админ открывает черновик, читает, правит и уже потом переносит в реальное задание.

## Рекомендуемые типы jobs

- `assignment_generate_from_text`
- `assignment_generate_from_file`
- `assignment_improve_existing`
- `assignment_analyze_existing`
- `submission_review`
- `user_risk_review`
- `support_message_review`
- `minecraft_chat_review`

## Пример ResultJson для генерации задания

```json
{
  "draft": {
    "courseId": "00000000-0000-0000-0000-000000000000",
    "assignmentType": "math",
    "title": "Исследование параболы",
    "description": "Сгенерировано AI на основе прикреплённого конспекта.",
    "settings": {
      "maxAttempts": 3,
      "passPercent": 60,
      "shuffleBlocks": false,
      "allowReview": true
    },
    "blocks": [
      {
        "blockType": "info",
        "title": "Условие",
        "promptContent": {
          "type": "doc",
          "content": []
        }
      }
    ]
  }
}
```

## Пример ResultJson для review решения

```json
{
  "userId": "00000000-0000-0000-0000-000000000000",
  "assignmentId": "00000000-0000-0000-0000-000000000000",
  "sourceType": "math",
  "sourceAttemptId": "00000000-0000-0000-0000-000000000000",
  "verdict": "suspicious",
  "score": 0.82,
  "summary": "Ответ слишком похож на шаблонное решение и был отправлен аномально быстро.",
  "signals": [
    { "code": "too_fast", "weight": 0.6 },
    { "code": "pattern_match", "weight": 0.22 }
  ]
}
```

## Отдельный AI-пользователь

В `Program.cs` добавлена заготовка под авто-создание системного пользователя по конфигу:

- `AI__Enabled=true`
- `AI__SystemUserEmail=taskforge.ai@system.local`
- `AI__SystemUserFirstName=TaskForge`
- `AI__SystemUserLastName=AI`

Такой пользователь создаётся с ролью `AiAgent` и нужен прежде всего для аудита и истории действий.


## Что добавлено во втором проходе

- Специализированные admin-endpoint'ы:
  - `POST /api/admin/ai/generate/from-text`
  - `POST /api/admin/ai/generate/from-file`
  - `POST /api/admin/ai/analyze-assignment`
  - `POST /api/admin/ai/review-submission`
  - `POST /api/admin/ai/review-user`
- Список AI-артефактов:
  - `GET /api/admin/ai/submission-reviews`
  - `GET /api/admin/ai/risk-reports`
  - `GET /api/admin/ai/assignment-insights`
- `AiJobService` теперь умеет собирать input не только из ручного JSON, но и из реальных сущностей TaskForge:
  - снимок существующего задания (test/math/code/image)
  - снимок попытки пользователя
  - агрегаты и recent activity пользователя
  - support/Minecraft summary для risk-review
- `taskforge-ai-worker` получил более умный prompt-builder: он даёт модели разные схемы output для генерации, анализа, review решений и review пользователей.


## Round 3 additions

- AI draft can now be published into a real `math` or `test` assignment via `POST /api/admin/ai/drafts/{id}/publish`.
- Assignment edit page has a quick **AI-аудит** action.
- Admin users page has a quick **AI risk** action.

## Round 4: self-check before publish

Теперь AI-генерация поддерживает `enableSelfCheck` для `math`, `test` и `code-test`.

- Для `code-test` worker ожидает `publicTests`, `hiddenTests` и `referenceSolutionPython`.
- После генерации worker сам прогоняет `referenceSolutionPython` по тестам и пишет результат в `draft.meta.selfCheck`.
- Для `math` и `test` worker делает структурную Python-проверку draft и тоже пишет результат в `draft.meta.selfCheck`.
- Для уже существующего draft есть отдельный job `assignment_validate_draft` и admin endpoint `POST /api/admin/ai/drafts/{id}/validate`.


## Self-check и публикация

Сейчас каркас работает по минимально завершённой схеме:

1. AI генерирует draft для `math` / `test` / `code-test`.
2. Worker сразу старается сделать `self-check` через Python.
3. В `draft.meta.selfCheck` сохраняется статус `passed / needs-review / failed`.
4. Публикация по умолчанию требует `passed`, если это не отключено в `AI:RequirePassedSelfCheckForPublish`.

Это даёт базовую защиту от публикации совсем сырых AI-черновиков.


## Deploying AI worker

### GitHub build

Теперь для `taskforge-ai-worker` есть отдельный GitHub Actions workflow `build-ai-worker.yml`. После push в `develop` он публикует образ:

- `ghcr.io/<owner>/taskforge-ai-worker:latest`

### Separate AI machine

Если нейронка развёрнута не на основном сервере, worker лучше запускать отдельным compose-стеком (`docker-compose.ai-worker.yaml`) на AI-хосте. В этом случае:

- `TASKFORGE_API_BASE` должен указывать на основной TaskForge API по сети;
- `TASKFORGE_INTERNAL_KEY` должен совпадать с `API_INTERNAL_KEY` backend-а;
- `OLLAMA_BASE_URL` указывает на локальный Ollama на AI-хосте или внешний URL модели.
