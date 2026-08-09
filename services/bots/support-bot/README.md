# support-bot

Telegram adapter for TaskForge.

This is the only service that receives `SUPPORT_BOT_TOKEN` / `Telegram__BotToken` and the only service allowed to call Telegram Bot API.

Responsibilities:

- Telegram account linking and private support messages;
- support group relay;
- delivery of password-recovery codes through the internal endpoint
  `POST /api/internal/password-recovery/send`;
- AI/crawler access telemetry aggregation and low-noise Telegram digests through
  `POST /api/internal/ai-access/events`.

The internal delivery endpoint is available only inside the Docker network and requires `X-Internal-Key`. `identity-api` owns password-recovery state and verification, but never receives the Telegram bot token and never calls Telegram directly.


AI access events are sanitized before they reach this service. The bot groups bursts into digests, masks IP addresses by default, suppresses isolated generic discovery hits, and never receives passwords, request bodies, cookies, Authorization/JWT values or Browser session tokens. See `TASKFORGE_AI_ACCESS_NOTIFICATIONS.md`.
