# Stage 4: retire the standalone CT/Masha application

Scope: **TaskForge MultiNode source branch only**. This is a cumulative addition to
Stages 1–3. Apply Stage 4 directly to a pristine checkout of the inspected
`PiVasya/taskforge` `develop` commit `8c1ddc0956c6f32e8803d4dc97352d4eae71b529`.
Do not apply earlier patch versions first.

## Retired components

- `apps/web-ct/` (standalone front-ct image and CT React SPA) — 74 files.
- `services/tasks/quiz-api/` (standalone CT quiz service) — 29 files.
- Their images in the selective/full GitHub Actions matrices and local `build.sh`.
- Their Docker Compose definitions, gateway dependencies, routing and HA readiness.
- The second CT hostname, TLS virtual host and the CT-specific certbot SAN.
- The `quiz-api` step in account merge/delete; the remaining account analyzer and
  account manager keep their existing routes and database.
- CT site entries in browser route discovery, site policy and URL checks.
- Standalone CT EF migration script entries, without deleting historical DB data.
- CT frontend dependency install/build/test in CI.

## Explicitly preserved

- **Main TaskForge SPA** at `apps/web/`, including normal course tests, their quiz
  UI (`apps/web/src/features/task-test/`), image, math, SQL and code tasks.
- **`tasks-api`**, including ordinary test/math attempts and all 5 assignment kinds.
- **`services/bots/telegram-quiz-bot/`**: separate Telegram bot and its own schema.
  The word `quiz` in this subsystem does NOT mean the retired CT quiz API.
- **`ai-api` account intelligence / Account Manager**, all account review workflows
  except the redundant quiz-api callback.
- **PostgreSQL tables, databases, data and Docker volumes**: no DROP TABLE,
  DELETE FROM, volume prune or destructive EF migration is included.
- Other browser-api features for the main site, and gateway health endpoints.

## Compatibility and required operator work

This is a source-level retirement for the **new branch**, not an operation against
running servers. The old production deployment is unchanged until an operator
intentionally switches to rebuilt images and updated stack configuration.

1. A running CT subdomain will no longer be served by this branch's gateway.
2. Before production cutover, preserve/back up any existing CT data if retention
   is required. This package DOES NOT move or delete it.
3. Remove historical CT DNS records/standalone origin certificates **manually**
   only after confirming the old site should actually go offline. Cloudflare
   and your live router are not modified.
4. Old manually installed host agent packages might still reference CT/quiz;
   update them in a separate controlled deployment task.
5. Do not drop quiz-api migrations from the existing production database by
   altering EF migration history. Removing a separate service source folder
   does not erase existing migration history in a deployed database.

## Application method and verification

Run `bash APPLY.sh /path/to/clean/develop-checkout`. The script computes Git blob
SHA-1 for *every file* in both retired subtrees (including images and icons),
checks each expected file, checks all modified source files, and refuses to change
anything if source differs or extra files exist. It never touches remote GitHub.

Locally verified: Node.js transformation/preflight tests, Bash/JS syntax, and ZIP
integrity. Full .NET compilation, Docker Compose validation and real integration
tests require an actual checkout and a CI runner with the required toolchains.

No changes here move the task graph, redefine assignment ownership or change CT
history in the live database.
