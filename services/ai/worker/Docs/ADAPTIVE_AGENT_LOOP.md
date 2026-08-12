# Adaptive agent loop

`AdaptiveAgentLoopWorkflow` is the top-level controller of `services/ai/worker`. The worker does not force every request through one hard-coded `analysis -> generation -> validation` chain. On each step the decision model selects a bounded package of actions from an explicit allowlist; the backend executes them in order and persists observations in `AgentLoopState`.

## Execution model

```text
claim run
-> inspect accumulated AgentLoopState
-> model chooses 1..MaxAgentActionsPerStep allowed actions
-> backend validates and executes actions sequentially
-> persist observations/working memory
-> repeat until finish or MaxAgentLoopSteps
```

Invalid JSON, an unavailable action or an LLM failure does not bypass the workflow: `AgentLoopDecisionClient` falls back to deterministic routing.

## Current action allowlist

The current code exposes exactly these 18 actions:

- `inspect_context` — normalize the run payload, conversation history, course/focus assignment context and useful memory;
- `classify_request` — determine intent/scenario, constraints and whether the request needs course context, generation, analysis or mass editing;
- `load_editable_assignments` — load and normalize all editable assignments available to the current course operation;
- `map_course_structure` — build the course map: order, types, languages, difficulty/rating and concept timeline;
- `extract_course_style` — infer the course's naming, descriptions, tags, tests, inline-code and teaching style;
- `find_learning_gaps` — identify missing bridges, difficulty jumps, weak tests and other course gaps;
- `analyze_assignment_complexity` — produce per-assignment complexity/reason/confidence data; required before mass rerating/difficulty work;
- `plan_course_enrichment` — combine request, course map, style and gap report into one working brief;
- `search_course` — search already loaded course content; accepts a `query` argument;
- `propose_assignment_patch_set` — produce a bounded patch set with diffs for mass changes such as rating/tag/title updates;
- `review_patch_set` — validate a pending patch set before the run can finish;
- `review_delegated_result` — validate a result returned by a specialized `delegate_*` workflow before finishing;
- `delegate_assignment_draft` — generate/revise assignment drafts through the validated draft workflow;
- `delegate_course_audit` — run the specialized course audit workflow;
- `delegate_course_edit` — prepare safe course edits without silently writing them;
- `delegate_polish_assignment` — revise selected assignment drafts;
- `answer_directly` — answer the user when no generator/validator/write workflow is needed;
- `finish` — finish only when a valid final message, reviewed delegated result or reviewed patch set exists.

Keep this list synchronized with `Workflows/AgentLoop/AgentLoopDecisionClient.cs` and `AdaptiveAgentLoopWorkflow.Actions.cs`. Do not document an action that is not in the allowlist and do not add an allowlisted action without documenting its guardrails here.

## Run memory

`AgentLoopState` persists the user request, course/assignment ids, payload, recent messages, outline/targets, intent, course map, style profile, gap report, enrichment brief, editable assignments, complexity report, pending patch set/review, delegated result/review, notes, decisions, observations and final message.

Before delegation, this state is forwarded as `agentLoopMemory` / `memory.agentLoop`, so specialized workflows operate on accumulated context rather than a bare prompt.

## Guardrails

- Actions outside the 18-action allowlist are rejected and deterministic fallback is used.
- Delegation is delayed until `inspect_context` and `classify_request` have run.
- Course-dependent generation should first build the map/style/brief when those are relevant.
- Mass rating/difficulty changes must load assignments, map the course, analyze complexity, propose a bounded patch set and run `review_patch_set`.
- `delegate_*` cannot be repeated after a delegated result already exists.
- `finish` is rejected when a delegated result has not passed `review_delegated_result`.
- `finish` is rejected when a pending patch set has not passed `review_patch_set`.
- Mass changes must remain patches/diffs; do not silently auto-apply broad edits from the decision loop.
- `MaxAgentActionsPerStep`, `MaxAgentLoopSteps`, context/state character limits and `MaxPatchOperationsPerRun` remain backend-enforced limits, not suggestions to the model.
- Draft validation/critic/repair stages remain authoritative. The agent loop chooses the route; it does not replace validators.

## Assignment solution material

`referenceSolution` is internal generation/validation material. It must never be used as a fallback for learner-visible `starterCode`. If no starter code was supplied, starter code stays empty. This invariant is enforced in the AI API mapping layer and must not be weakened by a future workflow refactor.
