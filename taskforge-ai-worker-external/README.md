# TaskForge external AI worker

This container is no longer a no-op reset stub. It now contains the first MVP of the external TaskForge AI agent runtime:

- durable-worker loop shell: `worker.py` claims internal agent jobs when `TASKFORGE_AGENT_API_BASE_URL` is configured;
- payload-driven local processing for smoke tests and early backend integration;
- `agent_core/` with runtime, context snapshot builder, chat memory patching, rule-based scenario router and validators;
- `scenarios/` with hardcoded MVP scenarios:
  - `course_analysis`;
  - `course_gap_audit`;
  - `guided_ladder`;
  - `style_matched_tasks`;
  - `bridge_tasks`;
  - `draft_revision`;
- `hardcoded_presets/ladder_screenshot_1.py` for the small friendly ladder style.

The worker still does **not** persist generated assignments into TaskForge. It returns structured artifacts such as `task_ladder_blueprint` and `task_draft_bundle`. Persistence/publishing should be added later through an internal Agent Action Gateway in the ASP.NET API.

## Runtime flow

```text
assistant_chat_turn job
  -> AgentRuntime
  -> MessageNormalizer
  -> ContextSupervisor
  -> ScenarioRouter
  -> Scenario pipeline
  -> Validators
  -> ResultEnvelope
```

A user request such as:

```text
Найди дырки перед if и сделай лесенку как на скрине 1
```

becomes a chained route:

```text
course_gap_audit -> guided_ladder
```

and returns a `ResultEnvelope` containing both the gap report and the ladder blueprint.

## Environment

OpenRouter/OpenAI-compatible LLM base is kept from the reset version:

```env
TASKFORGE_EXTERNAL_AI_BASE_URL=https://openrouter.ai/api/v1
TASKFORGE_EXTERNAL_AI_API_KEY=...
TASKFORGE_EXTERNAL_AI_MODEL=qwen/qwen3.6-plus
```

Internal TaskForge API settings are optional until the backend endpoints exist:

```env
TASKFORGE_AGENT_API_BASE_URL=http://taskforge:8080
TASKFORGE_AGENT_INTERNAL_KEY=...
TASKFORGE_AGENT_WORKER_ID=taskforge-ai-worker-external
TASKFORGE_AGENT_POLL_SECONDS=5
TASKFORGE_AGENT_HEARTBEAT_SECONDS=15
```

Without `TASKFORGE_AGENT_API_BASE_URL`, the worker starts safely, logs idle state, and does not try to mutate anything.

## Smoke test

From this folder:

```bash
/usr/bin/python3 worker.py --smoke
```

Expected behavior: a `course_gap_audit -> guided_ladder` result with a `task_ladder_blueprint`. The smoke test is deterministic and does not require an LLM API key.

## Backend contract expected later

The worker is already prepared for these internal endpoints:

```text
POST /api/internal/agent/claim-next
POST /api/internal/agent/runs/{id}/heartbeat
POST /api/internal/agent/runs/{id}/steps
POST /api/internal/agent/runs/{id}/complete
POST /api/internal/agent/runs/{id}/fail
```

The intended production design is still: external worker + internal action gateway + durable state machine + approvals + audit. The worker should call backend domain services through API actions, not write directly to PostgreSQL.
