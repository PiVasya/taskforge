# AI Foundry Wave 6 — Course Profile + Gap Analysis

В этой волне добавлен следующий крупный слой Foundry backend-only:

- `assignment_course_profile_build`
- `assignment_gap_analysis`
- интеграция этих стадий в batch-пайплайн перед `assignment_batch_plan`

## Что теперь происходит

1. Пользователь создаёт batch generation request.
2. Backend создаёт `AiBatch` и ставит job `assignment_course_profile_build`.
3. Worker строит `courseProfile` по `referenceAssignments`.
4. Backend сохраняет результат в `AiBatch.CourseProfileJson` и ставит `assignment_gap_analysis`.
5. Worker строит `gapAnalysis` по prompt + `courseProfile` + referenceAssignments.
6. Backend сохраняет результат в `AiBatch.GapAnalysisJson` и только после этого ставит `assignment_batch_plan`.
7. Дальше пайплайн идёт как раньше: plan -> brief -> draft -> reviews -> repair.

## Зачем это нужно

Это первый реальный переход от "generate package from prompt" к curriculum-aware planning:

- batch plan теперь может опираться не только на prompt,
- но и на профиль курса,
- а также на анализ дыр покрытия.

## Добавленные поля `AiBatch`

- `CourseProfileJson`
- `GapAnalysisJson`

## Новые stage/job type

### Job types
- `assignment_course_profile_build`
- `assignment_gap_analysis`

### Stages
- `course_profile_build`
- `gap_analysis`

## Ограничения текущей волны

- course profile и gap analysis пока ещё lightweight и largely AI/fallback-driven;
- нет отдельного persistence-слоя для ontology/gene graph;
- batch planner пока ещё не использует полный skill graph.

Следующий логичный шаг: `assignment_style_review` или полноценный `assignment_course_profile_build` с richer backend statistics.
