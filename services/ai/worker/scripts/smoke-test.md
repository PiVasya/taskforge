# Manual smoke test

1. Start the TaskForge backend dependencies and `ai-api`.
2. Start the current .NET worker from the repository root:

```bash
dotnet run --project services/ai/worker/TaskForge.AiAgent.csproj
```

3. In the AI UI ask:

```text
Проанализируй курс и найди пробелы
```

Expected behavior:
- the adaptive loop inspects/classifies context before delegating;
- course-dependent work builds the relevant map/style/gap context;
- delegated output is reviewed before `finish`;
- a course-audit/gap artifact is returned rather than an unreviewed raw model response.

4. Ask:

```text
Создай code-test задание по циклам для этого курса
```

Expected behavior:
- context/classification precede draft delegation;
- the draft passes validation/critic/repair gates;
- generated artifacts are reviewable/applicable through the AI API;
- internal `referenceSolution` is not leaked into learner `starterCode`.

5. For a mass rerating request, verify the path includes:

```text
load_editable_assignments
-> map_course_structure
-> analyze_assignment_complexity
-> propose_assignment_patch_set
-> review_patch_set
-> finish
```
