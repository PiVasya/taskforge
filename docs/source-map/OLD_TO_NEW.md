# Old to new source map

| Old path | New path |
|---|---|
| `clientapp` | `apps/web` |
| `clientapp-ct` | `apps/web-ct` |
| `nginx` | `apps/gateway` |
| `taskforge/Controllers/Auth`, `Users`, `Me/UiSettings` | `services/identity/api/extracted` |
| `taskforge/Controllers/Courses`, `UserGroups` | `services/education/api/extracted` |
| `learning-content-service` | `services/content/api` |
| `taskforge/Controllers/Assignments` | `services/tasks/assignment-api/extracted` |
| `quiz-task-service` | `services/tasks/quiz-api` |
| `taskforge/Controllers/Solutions`, `Leaderboard`, `Badges` | `services/solutions/api/extracted` |
| `taskforge/Controllers/Compiler`, `ImageRunners` | `services/execution/api/extracted` |
| `cpp-runner`, `java-runner`, `javascript-runner`, `pascal-runner`, `csharp-runner` | `services/execution/runners/*` |
| `image-cpp-runner`, `image-pascal-runner` | `services/execution/runners/*` |
| `code-analyzer` | `services/analyzers/code-analyzer` |
| `image-analyzer` | `services/analyzers/image-analyzer` |
| `taskforge/Controllers/Agent`, monolith Agent entities/services | `services/ai/api/extracted` |
| `taskforge-ai-agent-dotnet` | `services/ai/worker` |
| `taskforge/Controllers/Support` | `services/support/api/extracted` |
| `support-bot` | preserved in `docs/original/support-bot-legacy-source`; production adapter in `services/bots/support-bot` |
| `telegram-quiz-bot` | `services/bots/telegram-quiz-bot` |
| `taskforge/Controllers/Integrations/Minecraft*` | `services/minecraft/api/extracted` |
| `DefaultGroupAssigner`, `WorldLoaderFolia`, `minecraft-plugin-folia` | `plugins/minecraft/*` |
| `taskforge/Controllers/Files` | `services/files/api/extracted` |
| `taskforge/Controllers/Activity`, admin analytics/logs | `services/observability/api/extracted` |
