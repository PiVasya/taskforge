# Quiz validation policy

`quiz-api` must not rely on the CT frontend for data integrity.

Backend rules now enforced on admin create/update:

- `sectionCode` is normalized to uppercase before it is stored or used in filters.
- Valid section codes must look like `A1`, `A31`, `B1`, `B11`, etc.
- Any B-section task is forced to `text-answer`.
- Text answers require a non-empty `correctAnswer.value` / answer text.
- Choice tasks require non-empty `data.options`.
- Choice task `correctAnswer.selected` values must match existing options after trim/case/`ё` normalization.
- Single-choice tasks must contain exactly one correct answer.

This keeps direct API calls from bypassing the editor-side validation.
