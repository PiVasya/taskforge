# AI Foundry Runtime Wave 3

Эта маленькая волна добавляет следующую проверку Foundry-пайплайна:

- новый job type: `assignment_runtime_review`;
- новый stage: `runtime_review`;
- автоматическая постановка runtime-review после генерации draft;
- автоматическая постановка runtime-review после `assignment_repair`;
- участие runtime-review в repair-loop вместе со structural/pedagogy review.

Что проверяет runtime review:

- для `code-test`: прогон `referenceSolutionPython` по public/hidden tests и агрегированный verdict по runtime-suite;
- для `math`: наличие и проверяемость answer-blocks;
- для `test`: наличие и проверяемость questions.

Артефакты сохраняются в `AiArtifact` как обычные draft-review результаты и встраиваются в `draft.meta.aiReviews.runtime_review`.
