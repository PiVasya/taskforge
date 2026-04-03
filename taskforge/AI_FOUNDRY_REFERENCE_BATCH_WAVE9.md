# AI Foundry Wave 9

Backend-only update, no frontend and no migrations.

## Added
- `assignment_reference_pack_build`
- `assignment_batch_review`
- `reference_pack_build` stage
- `batch_review` stage

## What changed
- Batch item now passes through reference pack build before draft generation.
- Reference pack stores style/policy/negative/exemplar slices on `AiBatchItem`.
- Draft generation can use packed references instead of the raw large reference set.
- Batch review is enqueued when all batch items have reviewed drafts.
- Batch review stores coherence/progression/coverage summary on `AiBatch`.
- Added richer DTO/entity scaffolding for batch review and reference pack traces.
- Fixed some foundry glue issues in review flow and generation payload assembly.

## Notes
- No migrations were generated in this wave.
- Worker syntax verified with `py_compile`.
