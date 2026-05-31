# Frontend and microservice runtime compatibility fixes

The frontend still uses the legacy TaskForge HTTP contract, for example `/api/auth/login`,
`/api/courses`, `/api/courses/{id}/assignments`, `/api/assignments/{id}/submit`,
`/api/agent/...`, `/hubs/support` and `/hubs/minecraft-chat`.

This update keeps those frontend routes stable and makes the gateway split them to real
microservices instead of forcing the frontend to know service names.

## Fixed

- Gateway now has slashless and slash-compatible routing for legacy API paths.
- `identity-api` owns real auth/profile/ui-settings/admin-user endpoints.
- `education-api` owns courses/groups/group-members.
- `tasks-api` owns assignment catalog plus task-test/math-task compatibility endpoints.
- `solutions-api` owns submissions, user solution history, quotas, badges, leaderboard read model.
- `execution-api` owns compiler/image-runner routing and proxies to stateless runners.
- `ai-api` owns agent conversations and internal worker endpoints.
- `support-api` owns support tickets/messages and support SignalR hub.
- `minecraft-api` owns Minecraft linking/chat endpoints and SignalR hub.
- `files-api` owns file upload metadata endpoints.
- `observability-api` owns page views, system status and admin analytics.

## Important

Migrations are now tracked in the repository. After service schemas change,
so generate migrations locally before running a clean dev DB:

```bash
./scripts/generate-migrations.sh AddMeaningfulSchemaChange
```

For a clean dev run:

```bash
./deploy/dev/compose.sh down --remove-orphans -v
./deploy/dev/compose.sh up-logs --build
```

If you want to keep existing dev data, do not use `-v`.
