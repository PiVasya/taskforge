# AI Foundry Repair Wave 2

Что добавлено:
- автоматическая постановка `assignment_repair`, если structural/pedagogy review завершились с `failed` или `needs-review`;
- сохранение repair-артефакта в `AiArtifact`;
- обновление существующего `AiGeneratedAssignmentDraft` после `assignment_repair`;
- автоматический повторный запуск structural/pedagogy review после ремонта.

Ограничения волны:
- максимум 2 repair-цикла на draft;
- repair работает только в backend/worker, без UI;
- pedagogy review пока fallback/local, без отдельной сложной LLM-критики.
