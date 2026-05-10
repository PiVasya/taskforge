# Mapping to Microsoft Agent Framework

Implemented directly:

- `IChatClient` via `Microsoft.Extensions.AI.OpenAI`.
- `ChatClientAgent` / `AIAgent`.
- Function tools through `AIFunctionFactory.Create`.
- `AgentSession` serialization through `AgentSessionStore`.

Implemented as TaskForge-native layer for now:

- Context providers: `PromptContextComposer` + `AgentMemoryStore`.
- Workflow graph: `ITaskForgeWorkflow` + executors.
- HITL: `approval_request` artifacts and backend-owned writes.

Why not use only Agent Framework workflow API here? Because the existing backend already has queue/run/artifact persistence and the exact API surface may shift. This V2 keeps the production boundary stable while leaving a clear migration path to `WorkflowBuilder`/Durable workflows later.
