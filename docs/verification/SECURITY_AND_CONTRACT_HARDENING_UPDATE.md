# Security and contract hardening update

This update addresses the static audit findings from the microservice cut.

## Fixed

- Added `TaskForgeRequestSecurity` middleware to DB/API services.
- Admin routes now require an authenticated user with `Admin` role.
- Editor mutation routes now require `Admin`, `Editor`, or `LearningEditor` where applicable.
- User-owned routes now require a valid JWT from `Authorization: Bearer`, `tf_at` cookie, or SignalR `access_token` query.
- `/api/internal/agent/*` now requires `X-Internal-Key` and is no longer exposed by the public gateway route table.
- `ai-worker` and `ai-api` now receive the same internal key through compose.
- Public gateway routing no longer exposes `/api/internal/agent/*`.
- `files-api` now stores uploaded bytes in local persistent storage volume and serves `/api/private-files/{id}` from that storage.
- Fake-success endpoints were reduced:
  - AI generation/apply/polish endpoints return explicit `501` until the real pipeline is wired.
  - task/math attempt start/submit endpoints return explicit `501` until real attempt persistence is wired.
  - badge write/award/revoke endpoints return explicit `501` until badge persistence is wired.
  - solution submit no longer marks everything as `Accepted` or increments rating; it stores a queued submission.
- `identity-api` admin users endpoint now supports the frontend query contract: `query/q`, `role`, `linkedOnly`, `sortBy`, `sortDir`, `take`, and returns `{ items, stats }`.
- `observability-api` analytics overview now returns the richer object shape the frontend expects.
- `support-api` now scopes support tickets to the current user unless the caller is `Admin`.

## Still intentionally not complete

These areas now fail explicitly instead of returning fake success:

- real AI provider/queue execution;
- AI artifact apply;
- task/math attempt persistence;
- badge write/award/revoke workflows;
- full quota enforcement;
- full cross-service user display-name enrichment in leaderboard.

## Verification performed here

- YAML compose parse: ok.
- `.csproj` XML parse: ok.
- Security middleware presence in API services: ok.
- Public gateway no longer exposes `/api/internal/agent/*`: ok.
- Go runner tests: ok.
- Shell syntax for compose/verify scripts: ok.

`dotnet build` was not run in this environment because .NET SDK is unavailable here.
