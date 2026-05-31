# GitHub Actions image-level selective build

Updated `.github/workflows/develop-build.yml` so GitHub Actions can build every custom project image, but only when the corresponding image source changes.

## What changed

- Removed broad domain-level filters such as `services/tasks/**`, `services/execution/**`, `services/ai/**`.
- Removed `dorny/paths-filter` from the workflow.
- Added an explicit image manifest with 30 custom images.
- Each image has its own narrow path list.
- A change in `services/tasks/assignment-api/**` builds only `tasks-api`.
- A change in `services/tasks/quiz-api/**` builds only `quiz-api`.
- A change in one runner directory builds only that runner.
- Manual `workflow_dispatch` supports:
  - `build_all=true` to build all custom images;
  - `images=identity-api,front,ai-worker` to build a selected list.
- GHCR image repository names are normalized to lowercase before tagging.

## Custom images covered

The CI manifest covers all 30 custom images referenced by production compose:

- gateway
- front
- front-ct
- identity-api
- education-api
- content-api
- tasks-api
- quiz-api
- solutions-api
- rating-worker
- execution-api
- execution-worker
- csharp-runner
- cpp-runner
- java-runner
- javascript-runner
- pascal-runner
- image-cpp-runner
- image-pascal-runner
- ai-api
- ai-worker
- code-analyzer
- image-analyzer
- support-api
- support-bot
- minecraft-api
- files-api
- notifications-api
- observability-api
- telegram-quiz-bot

Infrastructure images like Postgres, RabbitMQ, MinIO, Caddy/Certbot base images are not built by this workflow because they are upstream images.

## Verification

`./scripts/verify-structure.sh` now validates:

- production image names match the CI image manifest;
- the CI manifest contains exactly 30 custom build images;
- broad domain filters are not used;
- manual full build and manual selected-image build are available;
- GHCR tags are lowercase-safe.
