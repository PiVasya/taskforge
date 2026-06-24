# Assignment analytics stage 3

## Что доделано

- Исправлены синтаксические ошибки предыдущего stage: лишняя сигнатура `CleanEventUid`, лишний `{` в `InsightsEndpoints`, дублирующий `<Card>` в `AdminAssignmentInsightsPage`.
- Миграции не добавлялись и snapshot EF не изменялся.
- Proctoring-события стали стабильнее: дедупликация по `eventUid`, sequence, active/hidden/blur duration.
- Test/math решения теперь отправляют answer draft события:
  - `test_answers_changed`
  - `test_answers_final`
  - `math_answers_changed`
  - `math_answers_final`
- Backend умеет фильтровать и хранить эти события согласно `analyticsSettings` задания.
- Timeline и suspicious events могут показывать sample текстового ответа, если это разрешено настройками хранения.
- Live SignalR dashboard теперь показывает состояние подключения: connecting / connected / reconnecting / offline.
- Suspicious sessions больше не выводит все сессии подряд: только с risk/clipboard/focus/visibility/fullscreen/code-change активностью.

## Миграции

Новых migration-файлов нет. После применения архива миграцию генерировать вручную:

```bash
dotnet ef migrations add AddAssignmentAnalytics \
  --project services/tasks/assignment-api/TaskForge.Tasks.Api.csproj

dotnet ef database update \
  --project services/tasks/assignment-api/TaskForge.Tasks.Api.csproj
```
