# AI generic placement fix

This patch removes the topic-specific placement resolver that was added for the previous matrix-related debugging case.

## Fixed

- Removed the dedicated `_placement_before_matrix` helper.
- Added `_placement_from_user_anchor`, a topic-agnostic resolver for phrases such as:
  - `перед <темой>`
  - `до <раздела>`
  - `после <задания>`
  - `before <topic>`
  - `after <topic>`
- The resolver extracts the anchor phrase from the user message and searches assignment title, description preview, content summary, tags and concept hints.
- The worker prompt now tells the model to support any course topic, not only matrices.

## Scope

The AI can now infer insertion placement for matrices, functions, cycles, strings, classes, tests, recursion, OOP, or any other topic that appears in `courseMap` / `courseContexts`.

## Migrations

No EF migrations were added.
