# AI Foundry Batch Wave 5

Что добавлено в этой волне:

- `AiBatch` и `AiBatchItem` как foundation для batch-generation.
- Backend endpoints:
  - `POST /api/admin/ai/batches/generate`
  - `GET /api/admin/ai/batches`
  - `GET /api/admin/ai/batches/{id}`
- Новый pipeline:
  - `assignment_batch_plan`
  - `assignment_brief_generate`
  - затем обычная draft-generation на каждый batch item.
- У draft добавлены связи с `BatchId` / `BatchItemId`.
- Добавлен `assignment_test_strength_review` в review chain и repair loop.

Что это дает:

- Можно запускать не только одиночную генерацию, но и parent-batch orchestration.
- На backend уже есть контейнер, план и отдельные элементы пакета.
- Каждый элемент batch после планирования получает свой brief и отдельную генерацию.
- Review chain стал сильнее: structural + pedagogy + similarity + test strength + runtime.

Что еще не сделано:

- полноценный course genome / gap analysis;
- batch-level coherence review;
- mutation testing;
- frontend отображение Batch Lab.
