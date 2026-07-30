# support-bot

Telegram adapter for TaskForge.

This is the only service that receives `SUPPORT_BOT_TOKEN` / `Telegram__BotToken` and the only service allowed to call Telegram Bot API.

Responsibilities:

- Telegram account linking and private support messages;
- support group relay;
- delivery of password-recovery codes through the internal endpoint
  `POST /api/internal/password-recovery/send`.

The internal delivery endpoint is available only inside the Docker network and requires `X-Internal-Key`. `identity-api` owns password-recovery state and verification, but never receives the Telegram bot token and never calls Telegram directly.
