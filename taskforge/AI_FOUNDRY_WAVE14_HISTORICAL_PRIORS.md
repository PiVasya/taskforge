# TaskForge AI Foundry — Wave 14

## Что добавлено
- `HistoricalPlannerPriorsJson` в `AiBatch`.
- History-aware planner priors mining из предыдущих batch'ей того же курса и типа задания.
- Проброс `historicalPlannerPriors` в:
  - batch plan
  - batch replan
  - brief generate
  - brief review
  - brief repair -> brief review
  - reference pack build
  - draft generation additional payload
  - planner feedback payload
- Усилен worker:
  - batch plan prompt теперь явно использует historical planner priors;
  - brief prompt учитывает historical / institutional / anti-pattern memory;
  - добавлен `build_reference_pack_prompt`;
  - fallback batch planning использует history-aware seeds и избегает weak patterns.

## Зачем это нужно
До этой волны память Foundry в основном была памятью текущего batch.
Теперь planner и downstream-этапы получают priors из прошлых batch waves:
- какие skill patterns были сильными,
- какие были слабыми,
- какие repair routes повторялись,
- какие anti-patterns и risky transitions уже встречались.

Это усиливает не только explainability, но и реальную повторную обучаемость orchestration-слоя.

## Что дальше логично делать
- deeper reuse of strong historical anchors прямо в planner ranking;
- historical suppression of weak transitions уже на уровне replan decisions;
- richer historical candidate selection для reference pack и draft generation.
