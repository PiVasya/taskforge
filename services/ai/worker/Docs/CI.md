# CI

There is no standalone `build-dotnet-ai-agent.yml` workflow in the current repository.

The .NET AI services are built by the normal image workflows:

- `.github/workflows/develop-build.yml` — selective image build; includes `ai-api` and `ai-worker` image definitions.
- `.github/workflows/develop-full-rebuild.yml` — full rebuild matrix; includes both `ai-api` and `ai-worker`.

The worker project is `services/ai/worker/TaskForge.AiAgent.csproj`. Keep CI documentation aligned with those workflows instead of adding references to deleted Python/legacy agent paths.
