# Microservice long-file restructuring update

Цель апдейта — убрать оставшиеся монолитные файлы внутри микросервисов после первого разнесения `Program.cs`.

## Что было найдено

Самые длинные рабочие файлы до рефакторинга:

| Файл | Было строк | Что сделано |
|---|---:|---|
| `services/ai/worker/Workflows/Executors/DraftAuthorExecutor.cs` | 1394 | Разделён на генерацию, prompting, parsing, metadata, tutorial-style и student-facing text |
| `services/bots/telegram-quiz-bot/Bot/TeacherBotHostedService.cs` | 1317 | Разделён на messages, callbacks, drafts, home, quizzes, students и technical/formatting |
| `services/ai/worker/Workflows/AgentLoop/CourseAgentTools.cs` | 985 | Разделён на course map/style, gaps, patches, enrichment/search, shape/style helpers, collection и text/json helpers |
| `services/ai/worker/Workflows/Executors/CourseSkillAnalyzer.cs` | 862 | Разделён на static detector, model input, model map, candidates, text skills, json helpers; `CourseSkillBridgeContext` вынесен отдельно |
| `services/bots/telegram-quiz-bot/Bot/StudentBotHostedService.cs` | 788 | Разделён на messages, callbacks, quiz flow, stats и UI |
| `services/execution/worker/Worker.cs` | 710 | Разделён на lifecycle/http pipeline, policy, tests, sanitization и contracts |
| `services/ai/worker/Workflows/AgentLoop/AdaptiveAgentLoopWorkflow.cs` | 608 | Разделён на loop, actions, delegation и result building |
| `services/tasks/assignment-api/Services/Image/AssignmentApiImageService.cs` | 565 | Разделён на render/compare, test cases, storage/upload и materialization |
| `services/ai/worker/Workflows/Executors/DraftCriticExecutor.cs` | 550 | Разделён на execution, bridge consistency, skill guards и parsing |

## Итог

- Рабочих `.cs/.ts/.tsx` файлов на 1000+ строк больше нет.
- Рабочих файлов на 600+ строк больше нет.
- Самые большие оставшиеся файлы 500+ строк — это EF migrations/model snapshots и одинаковые diagnostic/security helpers, то есть не бизнес-сценарии на 1000+ строк.
- В активной бизнес-логике после прохода самый большой файл стал около 446 строк.

## Принцип разнесения

Код не резался случайно по количеству строк. Файлы разделены по зонам ответственности:

- `Messages` — обработка входящих сообщений;
- `Callbacks` — обработка inline callback-ов;
- `QuizFlow` / `Quizzes` — сценарии вопросов и викторин;
- `Stats` — статистика;
- `Policy` — policy/analyzer проверки;
- `Tests` — нормализация тест-кейсов;
- `Sanitization` — зачистка runner output;
- `Parsing` — JSON parsing и fallback parsing;
- `Metadata` — bridge/source metadata;
- `TutorialStyle` — student-facing tutorial text generation.

## Проверки в этой среде

В среде нет `dotnet`, поэтому полноценный `dotnet build` не запускался. Выполнены статические проверки:

- повторный обход всех `.cs/.ts/.tsx` файлов по количеству строк;
- проверка отсутствия файлов на 1000+ и 600+ строк;
- грубая проверка баланса фигурных скобок в изменённых C# файлах с игнорированием строк и комментариев.
