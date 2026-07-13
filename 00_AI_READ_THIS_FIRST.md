# AI rules for this project

Open this file before changing the project.

## Hard rules

- Do not generate database migrations unless the user explicitly asks for migrations.
- Current logging policy is development mode: Docker images and Compose runtimes must keep `TASKFORGE_BUILD_DEBUG_LOGS=1` / `TASKFORGE_DEBUG_LOGS=1`. Do not disable, quiet, or change these defaults to `0` unless the user explicitly asks to change the logging policy. Preserve this rule whenever editing workflows, Dockerfiles, Compose files, or `.env.example` files.
- Do not edit existing migration files or ModelSnapshot files. If a model/schema change needs a migration, tell the user the exact command to generate it themselves instead of creating or modifying migration files in the archive.
- Do not put changelog text, update notes, "what was fixed", or authoring explanations into exported JSON, import examples, API responses, or user-visible UI.
- Do not leave frontend text that explains internal implementation history to users.
- Do not write comments like "made it nicer", "fixed here", "old update", "new format", or similar change-log notes in code.
- Keep admin screens compact. Put documentation and examples behind a small docs/help button.
- JSON examples must contain only canonical editable fields.
- Keep legacy import aliases in code only when needed for compatibility; do not advertise them in UI or fresh exports.

## JSON import/export shape

Canonical assignment export/import:

```json
{
  "schemaVersion": 2,
  "format": "taskforge-course-assignment-import",
  "courseId": "...",
  "exportedAt": "...",
  "assignments": []
}
```

Type-specific fields:

- `code-test`: `testCases`
- `image-test`: `testCases`, `imageTestReferenceKey`, `imageTestSimilarityThreshold`
- `test`: `testSettings`, `questions`
- `math`: `testSettings`, `blocks`
