# TaskForge AI Foundry — Wave 11

Что добавлено в этой волне:

- `assignment_student_journey_review` после `assignment_batch_review`;
- `assignment_batch_publish_prepare` как stronger publish gate;
- batch summary upgraded до `wave11-publication-memory`;
- batch-level memory groundwork:
  - `PositiveMemoryJson`
  - `BatchMemoryJson`
- новые batch поля:
  - `StudentJourneyJson`
  - `PublicationAuditJson`
  - `PositiveMemoryJson`
  - `BatchMemoryJson`
- batch re-review теперь не блокируется уже завершённым `assignment_batch_review`: после repair/повторных review chain batch может быть переоценён заново;
- mutation groundwork усилен семействами мутантов и более жёсткими сигналами test-strength.

## Логика

### 1. Student Journey Review

Batch после coherence-review проходит отдельную симуляцию движения студента по соседним задачам:

- резкие скачки сложности;
- слабые prerequisite-опоры;
- повтор соседних skill-anchor'ов;
- missing bridge steps.

### 2. Publication Gate

После batch review и student journey система готовит publication audit:

- average item quality;
- batch coherence;
- student journey safety;
- слабые items ниже quality floor.

На выходе — `publicationDecision.readiness`:

- `ready`
- `partial`
- `repair-needed`
- `blocked`

### 3. Memory Groundwork

Batch теперь складывает два memory-layer JSON:

- `PositiveMemoryJson` — сильные items и сильные dimensions;
- `BatchMemoryJson` — risky transitions, типовые repair routes, weak items, student journey signals.

Это не полноценная institutional memory, но уже хорошая основа для следующей волны.
