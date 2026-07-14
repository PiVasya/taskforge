# AI rules for this project

Open this file before changing the project.

## Hard rules

- Do not generate database migrations unless the user explicitly asks for migrations.
- Current logging policy is development mode: Docker images and Compose runtimes must keep `TASKFORGE_BUILD_DEBUG_LOGS=1` / `TASKFORGE_DEBUG_LOGS=1`. Do not disable, quiet, or change these defaults to `0` unless the user explicitly asks to change the logging policy. Preserve this rule whenever editing workflows, Dockerfiles, Compose files, or `.env.example` files.
- Do not edit existing migration files or ModelSnapshot files. If a model/schema change needs a migration, tell the user the exact command to generate it themselves instead of creating or modifying migration files in the archive.
- Do not put changelog text, update notes, "what was fixed", or authoring explanations into exported JSON, import examples, API responses, or user-visible UI.
- Do not leave frontend text that explains internal implementation history to users.
- Do not write comments like "made it nicer", "fixed here", "old update", "new format", or similar change-log notes in code.
- Keep admin screens compact. Put documentation and examples behind a small docs/help button.
- JSON examples must contain only canonical editable fields.
- Keep legacy import aliases in code only when needed for compatibility; do not advertise them in UI or fresh exports.

## JSON import/export shape

Canonical assignment export/import:

```json
{
  "schemaVersion": 2,
  "format": "taskforge-course-assignment-import",
  "courseId": "...",
  "exportedAt": "...",
  "assignments": []
}
```

Type-specific fields:

- `code-test`: `testCases`
- `image-test`: `testCases`, `imageTestReferenceKey`, `imageTestSimilarityThreshold`
- `test`: `testSettings`, `questions`
- `math`: `testSettings`, `blocks`

## Minecraft plugin logging policy

- Пока проект находится в разработке, `TaskForgeLink` должен собираться и поставляться с максимально подробными безопасными логами.
- В `plugins/minecraft/minecraft-plugin-folia/TaskForgeFoliaPlugin/src/main/resources/config.yml` сохранять `debug.enabled=true`, а также `debug.http`, `debug.httpBodies`, `debug.deathRecovery`, `debug.scheduler`, `debug.journal` и `debug.heartbeat` равными `true`.
- Логировать запуск, конфигурацию без раскрытия секретов, HTTP URI/метод/status/body preview, переходы death-recovery, планировщики Folia, запись журнала, повторные попытки и доставку сообщений игроку.
- Никогда не печатать полные `MINECRAFT_PLUGIN_KEY`, `MINECRAFT_WEBHOOK_KEY`, `security.taskforgeKey`, `taskforge.pluginKey` или другие секреты. Разрешены только наличие, длина и короткий SHA-256 fingerprint.
- Не отключать и не уменьшать эти логи, пока пользователь явно не попросит изменить политику логирования.
- В репозитории Minecraft должны оставаться только два собираемых плагина: `TaskForgeLink` и `CustomMobTweaks`. Не возвращать `DefaultGroupAssigner`, `WorldLoaderFolia` или другие JAR без прямой просьбы пользователя.
- Текущая ветка `CustomMobTweaks` — версия `2.1.0` для Folia `26.1.2` / Java `25`. Не откатывать её к старой реализации `1.1.x` и не менять имя выходного JAR `CustomMobTweaks-*.jar` без прямой просьбы пользователя.
- Текущая ветка `TaskForgeLink` — версия `1.8.0` для Folia `26.1.2` / Java `25`. Сохранять полный hot-reload runtime через `/tflink reload`.
- Minecraft link codes are delivery-only: the website must never display, copy, or return a fallback/backup code to the browser. A generated code must be sent directly to the online Minecraft player through the authenticated webhook. If delivery fails, invalidate the generated code and return a clear error.
- Production defaults for direct site-to-Minecraft delivery are `http://mc.taskforge.by:25566` and `/taskforge/link/send`; health is `http://mc.taskforge.by:25566/health`. Do not blank these defaults unless the user explicitly changes the deployment topology.
- Keep detailed safe logs for link-code generation and delivery: resolved URL/path, request ID, HTTP status, elapsed time, response preview, exception type/message, and secret presence/length/fingerprint. Never log the raw code or secret.

## Minecraft death-recovery safety policy

- Never clear `PlayerDeathEvent#getDrops()` unless the current online-session cache has an authoritative `LINKED` result for that exact UUID.
- `UNLINKED` and `UNKNOWN` players must keep fully vanilla death behavior: do not serialize items, do not create a death-recovery journal row, do not create a backend row, do not alter experience, and do not delay drops.
- After an unlinked death, show a concise message after respawn explaining that linking `taskforge.by` enables coordinates, death chests, and return-to-death actions.
- If link status cannot be verified, prefer vanilla drops over interception. A previously confirmed linked state may be preserved during a transient refresh failure for the current online session.
- The backend must reject creation of a new death-recovery row for an unlinked player and must return an explicit `not-linked` reason.
- Keep verbose diagnostics for link-cache transitions, death gating, vanilla-drop preservation, backend rejection, scheduler delivery, and recovery state transitions until the user explicitly changes the logging policy.
- Every periodic player-status request from the Minecraft plugin must send both the exact UUID and the current nickname. UUID is authoritative; exact-nick fallback is allowed only for one active link whose UUID is still empty, after which the backend must persist the exact UUID.
- The normal user-facing Minecraft settings/API status must expose only link identity and the remaining Minecraft balance. Do not show or return base rating, spent/restored totals, effective rating, or action prices to ordinary users. Admin-only screens may keep operational detail.
- A TaskForge user may have any number of simultaneously active Minecraft nickname/UUID links. Never reintroduce a numeric link cap or force the user to unlink one profile before adding another.
- One exact Minecraft UUID may have only one active owner. Re-linking an already active UUID must be rejected, including when it already belongs to the same TaskForge account, so duplicate rows cannot appear.
- Minecraft balance is one shared user-level ledger keyed only by TaskForge `UserId`; all linked Minecraft profiles spend from the same balance. Adding, removing, renaming, unlinking, or re-linking Minecraft profiles must never create, delete, rewrite, reset, refund, or restore `MinecraftRatingTransactions`.
- Remove the `Minecraft` feature role only when the user's last active Minecraft link is removed. Per-link unlink must preserve the role while any other active link remains.
- A single death offer may execute several actions sequentially. Coordinates are independent from item handling; after buying coordinates the player may still buy a chest, return, or choose ordinary drops while the offer is open.
- Never execute two paid death actions concurrently for the same death. Queue only one active action at a time and tell the player which action is currently running. This protects per-action idempotency and compensation state.
- A denied action, including insufficient balance, must not resolve items, close the offer, or block later choices. Show the exact reason, required cost and available balance when supplied by the backend, then show the remaining actions once.
- Ordinary drops resolve only the item-storage choice. Coordinates and return may remain available. A paid return releases unresolved items at the exact death point before spectator rescue, so a chest is no longer available afterward.
- Every paid death action uses its own deterministic idempotency key derived from `deathId + action`. Compensation must target only that action. Never use link/unlink or another action's refund to restore the whole shared Minecraft balance.
- Do not reprint death offers on unchanged periodic link-status refreshes. Show on respawn/link transition/join, after an explicit action result, or after reconnect; repeated background refreshes must be suppressed.


## Minecraft plugin config reload rule

Both retained Minecraft plugins must support applying `config.yml` changes without a full server restart. Do not remove or weaken these commands unless the user explicitly requests it:

- `TaskForgeLink`: `/tflink reload` (full runtime reload: HTTP listener, keys/URLs, chat poller, link cache schedules, death recovery settings and manager).
- `CustomMobTweaks`: `/cmt reload` (full component reload: unregister listeners, cancel tracked Folia tasks, reread config, reconstruct all modules).

A reload must not duplicate listeners or periodic tasks. It must emit detailed start/stop/result logs. New config-backed functionality should participate in the corresponding full reload lifecycle.

## Minecraft runtime invariants added in 2026-07

- TaskForgeLink death offers are delivered from Folia/Paper `PlayerPostRespawnEvent`, with `PlayerJoinEvent`, confirmed link transitions and hot-reload recovery as explicit secondary triggers. Never restore a global alive-player heartbeat scan for unseen offers.
- CustomMobTweaks sniper-arrow guidance must stop immediately on every projectile/entity impact, including cancelled shield blocks. Never allow a homing scheduler to keep moving an arrow after impact.
- The maintained Minecraft plugins are exactly `TaskForgeLink` and `CustomMobTweaks`; do not reintroduce WorldLoaderFolia or DefaultGroupAssigner.
