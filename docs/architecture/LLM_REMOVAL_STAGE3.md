# Removing the LLM subsystem from TaskForge's future multi-node application source

This package is **cumulative (stages 1 + 2 + 3)**, and must be applied to an unchanged
PiVasya/taskforge `develop` checkout. It changes source code; it neither updates
installed servers nor deletes data from PostgreSQL or MinIO.

## Deleted

- The entire `services/ai/worker/` tree: chat/LLM provider integration, agent loop,
  prompt/agent tools, draft generation and related tests.
- Assistant-only HTTP endpoints, internal worker endpoints, SignalR hub and
  investigation endpoints under `services/ai/api/`.
- Their worker-specific DTOs, mapping/serialization/dispatch helpers and LLM-only tests.
- The React assistant page, conversation composer, agent API and agent hub.
- AI assistant routes in frontend navigation, Nginx API/SignalR gateway,
  Compose service lists, build.sh and both Docker build workflows.
- LLM API keys, provider/model, wakeup and agent-loop configuration from the
  Compose definitions and `ai-api/appsettings.json`.

## Kept

- `services/ai/api/Services/AccountIntelligence/`: deterministic account analysis,
  account similarity engine and evidence, operator reviews and account lifecycle.
- `services/ai/api/Endpoints/AccountIntelligence/AccountIntelligenceEndpoints.cs`
  and `AccountLifecycleEndpoints.cs`, plus the admin Account Manager page.
- Its independent PostgreSQL-guarded worker scheduling from MultiNode Stage 2.
- `code-analyzer` for deterministic source-code security analysis and
  `image-analyzer` for image tasks. These are **not** the LLM assistant.
- `browser-api` Chromium automation and the AI-account-type compatibility feature
  for now: neither is a hosted language model, and deleting them safely would
  be a separate API/identity policy change.
- The existing `ai-api` image/DB/HTTP ownership identifiers for account-manager
  compatibility. They are **legacy names**, not an active LLM service.
- Every committed EF migration and ModelSnapshot. Historic conversation/agent
  tables remain mapped but no active agent endpoints can write new conversations.
  A schema purge needs operator-generated migrations and a backup/retention plan.

## Scope constraints / verification

1. Do not mix assistant-era frontend with this API image: `/api/agent/*` and
   `/hubs/agent` no longer exist. Deploy gateway/frontend and API together.
2. `Cluster__MultiNode=true` must retain Redis for interactive compiler tickets,
   and `Database__MigrateOnStartup=false` on every replicated API.
3. The first push of a new source branch builds to isolated GHCR names. `ai-worker`
   is absent from the build matrix and from Compose.
4. This patch does **not** remove the historic LLM tables or AI account-type
   policy. Do not edit EF migration files by hand. Future schema changes must
   follow the repository's migrations-generation procedure.
5. Existing `deploy/cluster` historical Python/Patroni scripts are not a future
   K3s deployment mechanism and must not be used to install this branch.

## Verification to run in a real checkout

```sh
node --test tools/source-transform.test.mjs tools/source-prune.test.mjs
node apps/web/scripts/check-architecture.js
dotnet test services/ai/api/tests/TaskForge.Ai.Api.Tests/TaskForge.Ai.Api.Tests.csproj -c Release
bash scripts/tests/repository.sh
bash scripts/tests/frontend.sh
bash scripts/tests/dotnet.sh
bash scripts/tests/compose.sh
```

The first line runs from the extracted patch folder; other commands run from the
patched full source root. Full .NET, web, Docker and database tests require the
user's CI environment; they were not executed by the authoring container.
