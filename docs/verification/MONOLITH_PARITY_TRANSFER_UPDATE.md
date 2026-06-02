# Monolith parity transfer update

This update moves more behaviour from the legacy monolith into bounded microservices without merging domains back together.

## Tasks service

`services/tasks/assignment-api` now owns interactive task attempts for:

- `test` assignments;
- `math` assignments.

Added persistent `TaskAttempts` table owned by the tasks service. The service now supports:

- start attempt;
- continue active attempt after refresh;
- max-attempt enforcement;
- time-limit handling;
- answer checking for choice, fill/text, numeric, ordering and matching blocks;
- student attempt list/review;
- admin attempt list/review/delete;
- assignment insights;
- editor save/load for test and math payloads.

Image-test reference upload and direct image-file comparison are also wired to `image-analyzer` for uploaded image comparison. Code-to-image comparison still requires the execution/screenshot pipeline and returns a staged error instead of a fake success.

## Solutions service

`services/solutions/api` now owns more monolith behaviours:

- code submission persistence;
- rating projection update on accepted submission;
- leaderboard output;
- badges;
- user badges;
- quota buckets;
- image-solution history tables.

Badges follow the legacy behaviour: SVG is stored as a `data:image/svg+xml;base64,...` value, which keeps badge rendering independent from files/minio.

Quotas now use persistent token buckets with the legacy bucket names `tasks` and `top`.

## AI service

`services/ai/api` now creates queued `AiRun` records when a user sends a message. `ai-worker` can claim runs through the internal API, complete/fail them, and the API writes the assistant response back into the conversation.

Internal agent endpoints still require `X-Internal-Key` and remain hidden from the public gateway.

## Migration note

New owned tables were added to tasks, solutions and ai services. Run the safe migration script before starting with an existing database:

```bash
./scripts/generate-migrations.sh MonolithParityTransfer_$(date +%Y%m%d_%H%M%S)
```

For a clean dev reset:

```bash
./deploy/dev/compose.sh down --remove-orphans -v
./scripts/generate-migrations.sh MonolithParityTransfer_$(date +%Y%m%d_%H%M%S)
./deploy/dev/compose.sh build --no-cache
./deploy/dev/compose.sh up-logs
```
