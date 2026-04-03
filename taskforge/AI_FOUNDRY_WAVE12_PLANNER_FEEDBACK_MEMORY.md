# AI Foundry Wave 12 — planner feedback, institutional memory, anti-pattern memory

В этой волне добавлено:

- `assignment_batch_planner_feedback` как новый batch-level decision слой после publication gate;
- `PlannerFeedbackJson` в `AiBatch`;
- `InstitutionalMemoryJson` в `AiBatch`;
- `AntiPatternMemoryJson` в `AiBatch`;
- feedback loop из publication/student-journey/batch-review обратно в `brief repair`;
- auto-routing слабых slots назад в brief layer по planner feedback;
- более глубокая batch summary агрегация с planner feedback и memory слоями;
- проброс memory/feedback контекста в brief/review/reference pack/generation input.

## Новая логика пайплайна

После:

- `assignment_batch_review`
- `assignment_student_journey_review`
- `assignment_batch_publish_prepare`

теперь запускается:

- `assignment_batch_planner_feedback`

Он собирает:

- publication readiness,
- weak items,
- risky transitions,
- recurring repair routes,
- anti-patterns,
- planner adjustments,
- slot recommendations.

## Что сохраняется в batch

### PlannerFeedbackJson
Структурированный результат planner feedback:

- `slotRecommendations`
- `plannerAdjustments`
- `antiPatterns`
- `decisionSummary`

### InstitutionalMemoryJson
Batch-level institutional memory:

- recurring repair routes,
- strong anchors,
- publication readiness,
- positive + batch memory aggregation,
- planner feedback signals.

### AntiPatternMemoryJson
Новая негативная память:

- publication blockers,
- batch review findings,
- journey findings,
- risky transitions,
- weak routes,
- planner anti-patterns,
- seed negative memory from course profile layer.

## Auto routing

Если planner feedback рекомендует `brief-repair` / `rewrite-brief` / bridge-level brief fixes,
backend автоматически:

- ставит `assignment_brief_repair` для конкретных batch items,
- пишет `AiDecisionLog`,
- переводит batch в `planner-rerouting` / `brief_repair` stage.

## Усиление контекста для следующих стадий

Теперь в:

- `assignment_brief_generate`
- `assignment_brief_review`
- `assignment_brief_repair`
- `assignment_reference_pack_build`
- `assignment_draft_generate` additional payload

пробрасываются:

- `positiveMemory`
- `batchMemory`
- `plannerFeedback`
- `antiPatternMemory`

Это делает следующий цикл generation/review/repair уже обучающимся на предыдущем batch.

## Зачем это нужно

Эта волна закрывает важный зазор Foundry-плана:

не просто оценить batch,
а вернуть выводы batch-level decision layer обратно в planner/brief слой,
чтобы система не только критиковала результат,
но и реально улучшала следующий цикл производства задач.
