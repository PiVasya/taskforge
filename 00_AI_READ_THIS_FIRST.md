# AI rules for this project

Open this file before changing the project.

## Hard rules

- Do not generate database migrations unless the user explicitly asks for migrations.
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
