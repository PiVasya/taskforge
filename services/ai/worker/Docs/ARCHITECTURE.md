# Architecture

## Target shape

```text
TaskForge UI
  -> ASP.NET Core backend
  -> /api/internal/agent queue API
  -> taskforge-ai-agent-dotnet
  -> Microsoft Agent Framework ChatClientAgent
  -> OpenRouter/OpenAI-compatible IChatClient
```

## Layers

### Runtime

`TaskForgeAgentWorker` polls existing backend API. It keeps heartbeat leases and passes jobs to `TaskForgeAgentRuntime`.

### Workflow Router

`TaskForgeWorkflowRouter` selects a coarse safety workflow. This is not the old Python scenario-router: it does not decide every LLM action. It chooses the safe orchestration boundary, then agents and tools operate inside it.

### Agent

`TaskForgeAgentFactory` creates `ChatClientAgent` with a `ChatClientBuilder(...).UseFunctionInvocation()` pipeline. Tools are C# methods with `[Description]`.

### Tools

- `CourseContextTools`: read current run/course context and assignment search.
- `AssignmentDraftTools`: build/normalize typed TaskForge draft objects.
- `ValidationTools`: deterministic draft validation and backend compiler test execution.
- `ApprovalTools`: produce human approval request payloads.
- `PersistenceTools`: direct writes are intentionally disabled by default.

### Context and memory

`PromptContextComposer` replaces a single huge prompt pack with a bounded contextual prompt. `AgentMemoryStore` stores lightweight in-memory memory patches per conversation. For production, replace it with DB persistence around `AgentConversation.MemoryJson` or a dedicated table.

### Write boundary

Worker can emit artifacts:

- `assignment_draft_ready`
- `polished_assignment_draft`
- `course_gap_audit`
- `course_edit_proposal`
- `approval_request`

Backend remains the owner of writes. Existing `InternalAgentController.Complete` can create hidden drafts from `assignment_draft_ready` / `polished_assignment_draft` artifacts. Course edit proposals stay as `course_edit_proposal` and must not be auto-applied by worker completion; they require a separate user-approved action.

## Why this is stronger than the first prototype

- Real workflow layer, not one generic agent call.
- Bounded draft repair loop.
- Deterministic validators and backend compiler test tool.
- Explicit write guard.
- Existing backend result envelope preserved.
- OpenRouter is swappable through `ITaskForgeChatClientFactory`.
