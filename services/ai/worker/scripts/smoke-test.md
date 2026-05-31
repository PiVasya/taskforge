# Manual smoke test

1. Start backend.
2. Start worker:

```bash
dotnet run --project taskforge-ai-agent-dotnet/TaskForge.AiAgent.csproj
```

3. In UI ask:

```text
Проанализируй курс и найди пробелы
```

Expected:
- steps: runtime -> workflow -> context -> planning -> audit -> final;
- artifact: `course_gap_audit`.

4. Ask:

```text
Создай code-test задание по циклам для этого курса
```

Expected:
- steps: runtime -> workflow -> context -> planning -> draft -> validation -> critic -> approval;
- artifacts: `assignment_draft_ready`, `approval_request`;
- backend may create hidden draft from `assignment_draft_ready`.
