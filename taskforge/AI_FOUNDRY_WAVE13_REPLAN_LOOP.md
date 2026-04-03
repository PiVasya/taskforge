# AI Foundry Wave 13 — batch replan loop + deeper memory reuse

Эта волна доводит batch-level feedback loop до следующего шага:
- planner feedback теперь может не только отправлять weak items в brief repair,
- но и запускать `assignment_batch_replan`,
- который перестраивает `PlanJson` и возвращает batch обратно в `brief_generate`.

## Что добавлено

### Новый job type / stage
- `assignment_batch_replan`
- `batch_replan`

### Новые batch-поля
- `ReplanHistoryJson`

## Что умеет replan loop

1. `planner feedback` анализирует batch review + student journey + publication audit.
2. Если видит сильный replan signal, backend **не делает auto-brief-reroute**, а ставит `batch_replan`.
3. `batch_replan`:
   - перестраивает `plan.tasks`,
   - снижает резкие jumps,
   - добавляет `replanNotes`,
   - отмечает `replannedIndices`.
4. Backend применяет новый план **без дублей item'ов по index**:
   - existing items переиспользуются по `Index`,
   - старые drafts отвязываются (`BatchItemId = null`, `Status = superseded`),
   - item fields reset под новый brief cycle,
   - stale items, которых больше нет в плане, удаляются.
5. После этого заново ставятся `brief_generate` jobs.

## Что ещё усилено

- `institutionalMemory`, `antiPatternMemory`, `summary` теперь учитывают `ReplanHistoryJson`.
- `batch plan` и `brief generate` получают больше long-loop memory context:
  - `institutionalMemory`
  - `antiPatternMemory`
  - `replanHistory`
- planner feedback больше не конфликтует с reroute/replan:
  - если нужен replan, авто-роутинг weak slots в brief repair пропускается.

## Зачем это важно

Это закрывает один из ключевых хвостов Foundry:
система теперь умеет не только критиковать batch на верхнем уровне,
но и **перепланировать batch как учебную систему**, а не только чинить отдельные drafts.
