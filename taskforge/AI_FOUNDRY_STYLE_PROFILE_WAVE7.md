# AI Foundry Wave 7

This wave intentionally adds **no EF migration files**.
Schema-related work is limited to entity/config scaffolding so migrations can be generated manually later.

## Added in this wave
- `assignment_style_review` stage/job type
- richer `courseProfile` and `gapAnalysis` payloads/signals
- backend scaffolding for:
  - `AiDecisionLog`
  - `AiReferenceSnapshot`
  - `AiReviewFinding`
- review findings persistence from review jobs
- reference snapshot persistence for course-profile / gap-analysis / batch-plan / brief stages
- decision log persistence for batch milestones

## Purpose
Bring the AI backend closer to Foundry's:
- style review
- decision transparency
- reference provenance
- richer world modeling

## Important
No migration was generated here on purpose.
