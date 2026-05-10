# TaskForge .NET AI Agent V2 applied

This archive has the V2 .NET AI agent applied on top of the uploaded legacy TaskForge archive.

## Main additions

- `taskforge-ai-agent-dotnet/` - new .NET Worker Service AI runtime.
- OpenRouter/OpenAI-compatible `IChatClient` provider.
- Agent factory, runtime, internal API client, session store and result envelope builder.
- Domain tools: course context, assignment drafts, validation, approval, persistence guard.
- Workflow layer: open chat, course audit, assignment draft generation, course edit approval flow.
- Safety layer: dangerous writes disabled by default; write operations return approval artifacts.
- CI: `.github/workflows/build-dotnet-ai-agent.yml`.
- Compose: `compose/ai.yaml` now uses the .NET agent; legacy Python compose moved to `compose/ai-python-deprecated.yaml`.

## Important check after unpacking

Run locally because this environment has no `dotnet` CLI installed:

```bash
dotnet restore taskforge-ai-agent-dotnet/TaskForge.AiAgent.csproj
dotnet build taskforge-ai-agent-dotnet/TaskForge.AiAgent.csproj -c Release
dotnet test taskforge-ai-agent-dotnet/tests/TaskForge.AiAgent.Tests/TaskForge.AiAgent.Tests.csproj -c Release
```

## Runtime configuration

Minimal environment variables:

```bash
TASKFORGE_AGENT_INTERNAL_KEY=...
TASKFORGE_DOTNET_AI_API_KEY=...
TASKFORGE_DOTNET_AI_MODEL=openai/gpt-4o-mini
```

OpenRouter remains the default provider through an OpenAI-compatible endpoint.

## Added hardening in this patched archive

- Existing course/assignment edits are no longer applied directly from a model artifact. They are saved as proposals and must be applied through the backend apply endpoint.
- New apply endpoints:
  - `POST /api/agent/artifacts/{artifactId}/apply`
  - `POST /api/agent/runs/{runId}/artifacts/{artifactId}/apply`
- Request body:

```json
{ "dryRun": true, "note": "optional UI note" }
```

Use `dryRun: true` to preview and validate a proposal without changing the database. Use `dryRun: false` or omit it to apply.

Before applying, the backend checks:

- the artifact belongs to the current user's AI conversation;
- the artifact belongs to the route run when `runId` is provided;
- the current user can edit the resolved course through `ICourseAccessService`;
- optional course-level fields use explicit keys only: `courseTitle`, `courseName`, `courseDescription`, `courseAbout`, `isPublic`;
- every assignment patch targets an assignment in that course;
- ordering patches contain only assignments from that course;
- code-test changes include at least 2 public and 2 hidden tests;
- hidden tests are not just copies of public tests;
- code-test changes include a reference solution;
- the reference solution passes the proposed tests through the existing compiler runners.

For hidden AI drafts, the `polish_assignment_draft` request carries `draftConversationContext`, style hints and persistent conversation memory so the draft can follow requirements mentioned earlier in chat.

## Additional hardening in the final-plus patch

- The AI chat UI now exposes proposal actions directly on `course_edit_proposal`, `assignment_update_batch`, `course_style_update` and applicable `approval_request` artifacts:
  - `проверить` sends `dryRun: true` and performs backend validation without DB writes;
  - `применить` sends `dryRun: false` and applies only after the same backend checks pass.
- After run completion the UI reloads the conversation silently so persisted artifact ids are available for apply actions.
- Proposal application is idempotent by default: the same artifact cannot be applied twice unless the caller explicitly passes `force: true`.
- Proposal payloads are capped to avoid oversized model output:
  - artifact data JSON: 1,000,000 chars;
  - assignment updates: 100;
  - order ids: 500;
  - code test cases: 50;
  - test input/output: 16,000 chars each;
  - reference solution: 120,000 chars.
- Empty proposals are rejected with a clear message instead of being treated as successfully applied.
- Duplicate ids in ordering patches are rejected instead of silently normalized.
- The old unused auto-apply method for `assignment_update_batch` was removed from `InternalAgentController` to prevent future accidental re-enablement.
- Apply audit steps now store sanitized notes and the `force` flag.

## Deep AI debugging added before applying the update

A dedicated debug-dump endpoint was added for investigating AI behavior before and after applying proposals:

- `GET /api/agent/conversations/{conversationId}/debug-dump?format=text`
- `GET /api/agent/conversations/{conversationId}/debug-dump?format=json`

The dump includes:

- conversation metadata and persistent memory;
- full message list with raw `DataJson`;
- all runs with raw `RequestJson`, `ResultJson`, `ErrorJson`, `DebugJson`;
- all steps, including hidden debug steps;
- all artifacts with raw `DataJson`, parsed data and payload sizes;
- hidden AI drafts created from the conversation;
- run summaries, durations, artifact type counts and failure/waiting-approval counters;
- course/assignment context attached to the chat.

The React AI page now has a `Copy AI dump` button in the top toolbar and a `Скопировать всё` button inside the AI logs drawer. The copied text merges the backend dump with a client-side snapshot: SignalR events, local messages/runs state, selected draft tasks, apply results, current URL context and pending file metadata.

Additional backend debug/audit steps were added:

- `worker_result_ingested` records result size, result hash and artifact summaries when a worker completes a run;
- `hidden_draft_not_created` records draft artifacts rejected by backend gates;
- `internal_run_tests` records runner checks requested by the AI microservice, including language, code hash, tests and runner results;
- `artifact_apply_http_request` records every dry-run/apply HTTP attempt, including failures.

These debug steps are hidden from the normal user flow (`IsVisibleToUser = false`) but appear in the debug dump and raw AI logs.
