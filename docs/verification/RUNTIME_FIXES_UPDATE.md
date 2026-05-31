# Runtime fixes update

This update addresses the issues observed after the stack started successfully:

## Fixed

1. `ai-worker` was repeatedly logging `404 NotFound` for `POST /api/internal/agent/claim-next`.
   - `ai-api` now exposes compatibility endpoints expected by the extracted .NET AI worker.
   - `claim-next` returns an empty queue (`job: null`) until real AI job dispatch is wired.
   - heartbeat, steps, complete, fail and run-tests endpoints now return structured responses instead of 404.

2. `telegram-quiz-bot` was shown as unhealthy while the process was running.
   - The runtime image contains `wget`, not `curl`.
   - Dev/prod compose healthchecks now use `wget` for `/ready`.
   - Empty Telegram tokens are treated as a disabled bot mode, not as a container failure.

3. Caddy frontend containers produced formatting warnings.
   - `apps/web/Caddyfile` and `apps/web-ct/Caddyfile` were normalized.

4. Content/quiz APIs produced ASP.NET DataProtection persistence warnings.
   - Added named volumes for `/root/.aspnet/DataProtection-Keys` for content and quiz services.

## Not changed

- EF migrations are tracked in the repository as `InitialMicroserviceSchema`.
- Telegram tokens are still optional in dev. Without tokens the service is healthy, but teacher/student bots are disabled.
- The AI worker does not process real jobs until `ai-api` dispatch/persistence is implemented.
