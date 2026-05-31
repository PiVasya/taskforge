# Quality gates

Before calling this production-ready:

- `dotnet restore taskforge-ai-agent-dotnet/TaskForge.AiAgent.csproj`
- `dotnet build taskforge-ai-agent-dotnet/TaskForge.AiAgent.csproj -c Release`
- Run tests in `tests/TaskForge.AiAgent.Tests`.
- Verify OpenRouter model supports function/tool calling.
- Verify backend receives artifacts and creates hidden drafts.
- Verify no direct write tool is enabled in production.
- Add persistent session store if worker restarts are common.

## Manual smoke test

Ask:

```text
Проанализируй курс и найди пробелы
```

Expected artifact: `course_gap_audit`.

Ask:

```text
Создай code-test задание по циклам для этого курса
```

Expected artifacts: `assignment_draft_ready`, `approval_request`.
