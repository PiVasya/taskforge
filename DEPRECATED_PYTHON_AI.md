# Deprecated Python AI worker

`taskforge-ai-worker-external` is kept only for rollback and historical reference.

New AI development should target:

```text
taskforge-ai-agent-dotnet/
```

The legacy Python GitHub Actions builder is disabled and manual-only. The split compose module `compose/ai.yaml` now points to the .NET agent. The old Python compose module is preserved as `compose/ai-python-deprecated.yaml`.
