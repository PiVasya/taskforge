# Batch actions and course patch sets

Adaptive agent loop теперь поддерживает пакет действий за один model decision.
Модель может вернуть `actions[]`, а backend выполнит их по порядку, сохраняя отдельный `AiStep` и trace для каждого действия.

Ключевые действия по курсу:

- `load_editable_assignments` — нормализует все доступные задания курса для анализа и безопасных правок.
- `analyze_assignment_complexity` — оценивает сложность каждого задания: порядок в курсе, difficulty, понятия, тесты, объём условия, referenceSolution.
- `propose_assignment_patch_set` — готовит patch set для массовых правок, например пересчёт рейтингов.
- `review_patch_set` — проверяет patch set перед завершением run.

Для запроса вроде «поменяй рейтинги всем заданиям курса по сложности» ожидаемый маршрут:

```text
inspect_context
classify_request
load_editable_assignments
map_course_structure
analyze_assignment_complexity
propose_assignment_patch_set
review_patch_set
finish
```

Patch set не применяется молча. Он сохраняется как artifact `course_patch_set` с GitHub-like diff, причинами изменений и dry-run/apply кнопками на фронте.
