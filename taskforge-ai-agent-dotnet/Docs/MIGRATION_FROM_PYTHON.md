# Migration from Python AI worker

## Keep

- `AgentConversation`, `AgentRun`, `AgentMessage`, `AgentStep`, `AgentRunArtifact`.
- Existing `/api/internal/agent/*` contract.
- Existing SignalR UI updates.
- Existing backend hidden draft creation from artifacts.

## Replace

| Old Python worker | New .NET agent |
|---|---|
| `worker.py` polling | `TaskForgeAgentWorker` |
| `ScenarioRouter` | `TaskForgeWorkflowRouter` + agent tool choice |
| `llm_json.py` | `ChatClientAgent` + structured envelope parser |
| `context_pack.py` | `PromptContextComposer` + tools |
| scenario scripts | workflows + executors + function tools |

## Suggested rollout

1. Keep Python worker disabled/deprecated.
2. Run `.NET agent` side-by-side in development.
3. Verify `open_chat` first.
4. Verify `course_audit`.
5. Verify `assignment_draft_workflow` with a small C++ task.
6. Only then wire approval UI for course edit flows.

## Env mapping

```text
TASKFORGE_EXTERNAL_AI_API_KEY -> TaskForgeAgent__ApiKey
TASKFORGE_EXTERNAL_AI_MODEL   -> TaskForgeAgent__Model
TASKFORGE_AGENT_INTERNAL_KEY  -> TaskForgeInternalApi__ApiKey
```
