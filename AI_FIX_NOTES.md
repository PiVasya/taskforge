# AI fixes for chat behavior

## What was wrong
- The Python worker had a large hardcoded fallback router that aggressively pushed course conversations into bridge-plan flows.
- The backend auto-agent loop kept continuing after audit/inspect steps, which made the chat feel scripted instead of conversational.
- The prompt heavily biased the model toward hidden multi-step planning instead of natural dialogue.

## What was changed
- `taskforge-ai-worker-external/worker.py`
  - Added explicit separation between intents: inspect existing tasks, audit gaps, build plan, generate.
  - Stopped converting generic course requests into bridge planning by default.
  - Listing requests like "изучи задачи курса и выведи их сюда" now route to `inspect_course_assignments`.
  - Short ambiguous follow-ups now prefer a clarification question instead of silent pipeline continuation.
  - Generation follow-up like "всё, делай саму задачу" now routes to direct generation instead of old bridge-plan automation.
  - Improved singular-vs-multiple detection so "саму задачу" is treated as one task.

- `taskforge/Services/AI/AiChatService.cs`
  - Added explicit `inspect` intent detection for existing task listing requests.
  - Restricted backend auto-continuation: it now auto-continues only for explicit `advance_agent_stage`, not for every audit/inspect action.
  - Adjusted next-step selection so inspect requests do not collapse into bridge planning.

- `taskforge-ai-worker-external/prompt_builder.py`
  - Rebalanced the chat prompt toward natural dialogue.
  - Added stronger guidance that inspect/list/show requests must prefer `inspect_course_assignments` and must not jump to bridge plans.
  - Reduced multi-action bias and hidden pipeline chaining.

- `taskforge-ai-worker-external/tests/test_chat_memory_routing.py`
  - Replaced bridge-centric routing tests with tests that cover inspect-first conversational behavior.

## Validation performed
- `python3 -m py_compile` on modified Python files.
- Unit tests:
  - `tests/test_chat_memory_routing.py`
  - `tests/test_prompt_builder_regressions.py`
  - `tests/test_chat_stage_config.py`
  - `tests/test_chat_strict_mode.py`
