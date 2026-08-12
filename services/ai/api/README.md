# TaskForge AI API

`services/ai/api` is the persistent HTTP/realtime boundary for TaskForge's in-product AI system. It owns AI conversations, messages, runs, run steps, attachments/artifacts, worker coordination and the admin Account Intelligence / AI Account Manager state. It does **not** execute the LLM workflow itself; runnable jobs are claimed and processed by `services/ai/worker`.

## Main responsibilities

- Persist conversations/messages and queue AI runs.
- Persist run steps and artifacts so the frontend can inspect progress and review/apply generated material.
- Expose realtime agent updates through the AI SignalR hub.
- Provide internal worker coordination under `/api/internal/agent/*`.
- Run Account Intelligence scanning and lifecycle workers for admin review/operations.
- Keep EF Core migrations for `AiDbContext` in this service and apply them through the normal service migration policy.

## User-facing agent API

The main route family is `/api/agent/*`, including:

- list/create/read conversations;
- add messages and attachments;
- queue assignment-polish runs;
- cancel runs;
- inspect conversation debug data where authorized;
- review/apply generated artifacts.

The API queues work; `services/ai/worker` performs the adaptive workflow and reports steps/results back.

## Worker boundary

The current internal prefix is **`/api/internal/agent/*`**, not the retired `/api/internal-agent/*` spelling. Current worker routes include claim-next, heartbeat, append-step, complete/fail and the internal run-tests bridge. These routes are internal-key protected and are not public agent/browser APIs.

## AI Account Manager / Account Intelligence

The admin UI route `/admin/ai/account-manager` is backed by `/api/admin/ai/account-manager/*`. The service supports:

- create/list/read Account Intelligence runs;
- list/read findings;
- record/remove finding and account decisions;
- list/update/delete reviews;
- list/block/unblock accounts;
- queue/list/read/update/retry/cancel/archive/restore lifecycle operations.

Lifecycle operations cover the admin workflows implemented by `AccountLifecycleCoordinator`/workers (for example merge/delete-style managed operations); do not bypass those coordinators with direct cross-service database edits.

`accountType=ai` remains an Identity marker, not an authorization role. Account Manager/verification state must not be confused with Admin/Editor permissions.

## Draft safety

AI draft payloads may contain an internal `referenceSolution` so the worker can validate generated code tasks. `AiApiMappingService` must **never** copy that value into learner-visible `starterCode`. Missing starter code maps to an empty string. Preserve this boundary when changing draft/apply mappings.

## Related components

- Worker: `services/ai/worker`
- Browser/crawler automation service: `services/browser/api`
- Browser machine discovery: `/.well-known/taskforge-ai.json`, `/llms.txt`, `/api/site/agent/playbook`
- Frontend AI pages: `/agent`, `/ai`, `/admin/ai/account-manager`
