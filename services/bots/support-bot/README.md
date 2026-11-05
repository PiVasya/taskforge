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

## Discord messenger integration

Discord is an optional secondary user messenger. The existing Telegram bot remains the
administrator's support relay, recovery-code transport, and cluster/AI notification target.
Users can link Telegram, Discord, or both, and select their preferred reply messenger.
Support HTTP endpoints and realtime hub admission require at least one link (admins exempt).

### Create a bot (no public webhook needed)

1. Visit https://discord.com/developers/applications and create a New Application named TaskForge.
2. In **Bot**, obtain/reset the **bot token**. Store it only in the server-side deployment `.env`; never paste it into frontend code or public chats.
3. Enable **Developer Mode** in Discord, open the bot user's profile and **Copy User ID** (a numeric snowflake). This is `DISCORD_BOT_USER_ID`. It is normally the same as the Application ID, but use the actual bot user ID.
4. In **Installation**, configure a **Guild Install** using scopes `bot` (and optionally `applications.commands`). Add the bot to the TaskForge Discord server through the official install link. For DMs-only support no privileged message-content intent is required; the gateway connects with `DIRECT_MESSAGES` intent only. There are no mandatory slash commands.
5. Publish a server invite if desired and set `DISCORD_SUPPORT_SERVER_INVITE` so users can find a shared server. The bot can receive DMs only when Discord's privacy rules permit contact (normally the user and bot share a server and DMs are allowed).
6. In your deployment `.env`:

```dotenv
DISCORD_BOT_TOKEN=<token from Bot page>
DISCORD_BOT_USER_ID=<bot user ID>
DISCORD_SUPPORT_SERVER_INVITE=https://discord.gg/<your-invite>
```

The support-bot process connects **outbound** to `wss://gateway.discord.gg`
(Discord Gateway v10) and sends DMs using `https://discord.com/api/v10/`.
No public Discord webhook, OAuth user-token flow, message-content privileged intent,
or gateway nginx route is required. Configure a single support-bot replica per environment
when practical; the Support database's unique Discord message ID prevents duplicate
support messages on gateway redelivery.

### Linking and support

1. On TaskForge website go to **Settings > Integrations > Discord > Generate code**.
2. Open the bot's Discord profile, start a DM and send only the 8-character code shown on the website (for example, `ABCD2345`). No prefix, slash command or `!link` is needed. `!help` shows instructions.
3. The code is valid for 10 minutes by default, hashed in the Identity database, limited by request and confirmation throttling and invalidated after use. Linking the same Discord user to a second TaskForge account is rejected.
4. If both Telegram and Discord are connected, choose the **primary messenger** in Settings > Integrations. Reply delivery goes to exactly that messenger; no automatic cross-posting to both.
5. The existing support web chat stays available if at least one messenger is linked. Discord DMs become normal support messages and are also forwarded to the admin Telegram group. Unlinking the last messenger blocks user support API endpoints and SignalR admission; existing history remains stored.

If a user blocks the bot or disables Discord DMs, Discord can return HTTP 403.
The reply will be marked undeliverable in the support backend; the user can switch
back to a linked Telegram account. Discord doesn't guarantee all DMs can be delivered.

### Database migration commands

Generate migrations from the updated source (do not change existing migration files):

```sh
./scripts/generate-migrations.sh AddDiscordMessengerLink identity
./scripts/generate-migrations.sh AddDiscordMessageDedup support
```

Both migrations must be committed/applied **before** starting the updated services.
With no `DISCORD_BOT_TOKEN`, Telegram remains operational and the Discord integration
stays offline; no new messenger links can be completed until the bot is configured.
