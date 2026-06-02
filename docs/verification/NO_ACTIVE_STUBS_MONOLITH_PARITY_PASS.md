# No active stubs / monolith parity pass

This pass removes the remaining active stub responses from compiled services and keeps service boundaries intact.

## Fixed in this pass

- `ai-api`
  - attachments now persist as AI conversation messages;
  - polish-task/polish-tasks now queue actual AI runs for the .NET worker;
  - artifacts are persisted from worker completion payloads;
  - artifact apply can dry-run and create assignments via `tasks-api` when artifact data contains course/task data;
  - internal `run-tests` bridge now calls `execution-api` instead of returning a not-wired response.

- `tasks-api`
  - removed unused `/api/admin/users/{userId}/groups` stub from tasks boundary;
  - image-test run-code, compare-code and submit-code now use image runners and image-analyzer;
  - gateway now routes `/api/assignments/{id}/image-test/*` to `tasks-api`, because tasks owns assignment reference state.

- `execution-api`
  - removed old fake image-test endpoints from execution boundary. Execution remains runner API only.

- `identity-api`
  - feature-role catalog is now persistent;
  - feature-role create/update/delete work;
  - user-role assignment uses `UserFeatureRoles` instead of a fixed read-only catalog;
  - JWT includes assigned feature roles.

- `observability-api`
  - page views store user/action/status metadata;
  - analytics overview/search/user detail endpoints return data derived from captured page-view events instead of empty arrays.

## Verification

Static checks performed:

- frontend `/api/*` calls have matching backend endpoints;
- active compiled services contain no `FeatureUnavailable`, `OperationUnavailable`, `NOT_WIRED`, `PENDING_PORT`, `PIPELINE_NOT_READY`, `Status501NotImplemented`, or obvious fake `passed=true/similarity=1.0` markers;
- gateway image-test route points to tasks-api;
- no `MIGRATIONS_REQUIRED.md` markers remain.

## Migration note

New EF model changes were introduced in:

- `identity-api` (`FeatureRoles`, `UserFeatureRoles`);
- `ai-api` (`AiArtifacts`);
- `observability-api` (`PageViews` extended fields).

Run:

```bash
./scripts/generate-migrations.sh NoActiveStubsParity_$(date +%Y%m%d_%H%M%S)
```

before starting the updated stack.
