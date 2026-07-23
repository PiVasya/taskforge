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

- `TaskForgeLink` должен сохранять подробную событийную диагностику цепочки смерти, но не создавать постоянный фоновый шум.
- В `plugins/minecraft/minecraft-plugin-folia/TaskForgeFoliaPlugin/src/main/resources/config.yml` сохранять `debug.enabled=true` и `debug.deathRecovery=true`.
- `debug.verboseHttp`, `debug.verboseHttpBodies`, `debug.verboseScheduler` и `debug.verboseJournal` по умолчанию должны быть `false`; включать их только для точечной диагностики.
- Не возвращать ежесекундный heartbeat-лог, глобальный обход смертей или периодические запросы статуса привязки. Диагностика death-recovery должна быть событийной: смерть, локальное сохранение, backend-результат, респавн, постановка меню, доставка/ошибка, выбор действия и завершение.
- Ответы backend с `itemsPayload` нельзя печатать целиком. Разрешены HTTP status, длина тела и безопасные скалярные поля.
- Никогда не печатать полные `MINECRAFT_PLUGIN_KEY`, `MINECRAFT_WEBHOOK_KEY`, `security.taskforgeKey`, `taskforge.pluginKey` или другие секреты. Разрешены только наличие, длина и короткий SHA-256 fingerprint.
- В репозитории Minecraft должны оставаться только два собираемых плагина: `TaskForgeLink` и `CustomMobTweaks`. Не возвращать `DefaultGroupAssigner`, `WorldLoaderFolia` или другие JAR без прямой просьбы пользователя.
- Текущая ветка `CustomMobTweaks` — версия `2.5.2` для Folia `26.1.2` / Java `25`. Не откатывать её к старой реализации `1.1.x` и не менять имя выходного JAR `CustomMobTweaks-*.jar` без прямой просьбы пользователя.
- Текущая ветка `TaskForgeLink` — версия `1.8.5` для Folia `26.1.2` / Java `25`. Сохранять полный hot-reload runtime через `/tflink reload`.
- Minecraft link codes are delivery-only: the website must never display, copy, or return a fallback/backup code to the browser. A generated code must be sent directly to the online Minecraft player through the authenticated webhook. If delivery fails, invalidate the generated code and return a clear error.
- Production defaults for direct site-to-Minecraft delivery are `http://mc.taskforge.by:25566` and `/taskforge/link/send`; health is `http://mc.taskforge.by:25566/health`. Do not blank these defaults unless the user explicitly changes the deployment topology.
- Keep detailed safe logs for link-code generation and delivery: resolved URL/path, request ID, HTTP status, elapsed time, response preview, exception type/message, and secret presence/length/fingerprint. Never log the raw code or secret.

## Minecraft death-recovery safety policy

- Never clear `PlayerDeathEvent#getDrops()` unless the current online-session cache has an authoritative `LINKED` result for that exact UUID.
- `UNLINKED` and `UNKNOWN` players must keep fully vanilla death behavior: do not serialize items, do not create a death-recovery journal row, do not create a backend row, do not alter experience, and do not delay drops.
- After an unlinked death, show a concise message after respawn explaining that linking `taskforge.by` enables coordinates, death chests, and return-to-death actions.
- If link status cannot be verified, prefer vanilla drops over interception. A previously confirmed linked state may be preserved during a transient refresh failure for the current online session.
- The backend must reject creation of a new death-recovery row for an unlinked player and must return an explicit `not-linked` reason.
- Keep focused event-driven diagnostics for death gating, local journal persistence, backend result, both respawn events, entity-scheduler delivery, offer display, player action and recovery completion. Do not log idle loops.
- Link status is refreshed only on explicit lifecycle events such as join, runtime reload, manual refresh, or an authoritative backend response. Every such request must send both the exact UUID and current nickname. UUID is authoritative; exact-nick fallback is allowed only for one active UUID-less link, after which the backend must persist the UUID.
- The normal user-facing Minecraft settings/API status must expose only link identity and the remaining Minecraft balance. Do not show or return base rating, spent/restored totals, effective rating, or action prices to ordinary users. Admin-only screens may keep operational detail.
- A TaskForge user may have any number of simultaneously active Minecraft nickname/UUID links. Never reintroduce a numeric link cap or force the user to unlink one profile before adding another.
- One exact Minecraft UUID may have only one active owner. Re-linking an already active UUID must be rejected, including when it already belongs to the same TaskForge account, so duplicate rows cannot appear.
- Minecraft balance is one shared user-level ledger keyed only by TaskForge `UserId`; all linked Minecraft profiles spend from the same balance. Adding, removing, renaming, unlinking, or re-linking Minecraft profiles must never create, delete, rewrite, reset, refund, or restore `MinecraftRatingTransactions`.
- Remove the `Minecraft` feature role only when the user's last active Minecraft link is removed. Per-link unlink must preserve the role while any other active link remains.
- Every death offer is single-choice. The first valid button click atomically stores the selected action and closes the offer before any backend request. Reconnects, backend pulls and double-clicks must never show or charge a second action for the same death.
- Coordinates are a complete choice: after successful purchase the coordinates are shown and the captured items are released at the death point. Chest, return, chest+return and ordinary drop are also terminal choices for that offer.
- A denied selected action, including insufficient balance, must not reopen the menu. Show the exact reason, then release unresolved items at the death point without charging.
- Every paid death action uses a deterministic idempotency key derived from `deathId + action`. The backend must reject a purchase whose action differs from the action already stored on the death row. Compensation targets only that selected action.
- Do not implement periodic link-status refreshes. Show an unclaimed death offer only on respawn, confirmed link transition, join, hot reload recovery, or the local watcher for that exact death while its player is still on the death screen.


## Minecraft plugin config reload rule

Both retained Minecraft plugins must support applying `config.yml` changes without a full server restart. Do not remove or weaken these commands unless the user explicitly requests it:

- `TaskForgeLink`: `/tflink reload` (full runtime reload: HTTP listener, keys/URLs, chat poller, event-driven link cache, death recovery settings and manager).
- `CustomMobTweaks`: `/cmt reload` (full component reload: unregister listeners, cancel tracked Folia tasks, reread config, reconstruct all modules).

A reload must not duplicate listeners or periodic tasks. It must emit detailed start/stop/result logs. New config-backed functionality should participate in the corresponding full reload lifecycle.

## Minecraft runtime invariants added in 2026-07

- TaskForgeLink death offers are delivered from standard `PlayerRespawnEvent` and Paper/Folia `PlayerPostRespawnEvent`, deduplicated per player. `PlayerJoinEvent`, confirmed link transitions and hot-reload recovery are secondary triggers. A local per-death respawn watcher may cover a missing event until that player actually respawns or the offer expires; it must never scan unrelated players, log idle ticks, or perform network requests while waiting.
- CustomMobTweaks sniper-arrow speed, aiming and homing apply only to the normal vanilla `SKELETON` entity. Never extend them to Stray, Bogged or Wither Skeleton. Guidance must stop immediately on every projectile/entity impact, including cancelled shield blocks.
- Radioactive zones are retired from CustomMobTweaks. Do not reintroduce `RadiationManager`, `radiation-zone` config sections, repeated zone knockback, forced fire, or random inventory item ejection.
- Breeze Elytra remains chance-based through `extra-loot.breeze.drops.elytra.chance` (default `0.05` = 5%); never force it to 100%. Preserve direct, projectile, and short delayed-death player attribution and event-driven `[loot]` roll diagnostics.
- The maintained Minecraft plugins are exactly `TaskForgeLink` and `CustomMobTweaks`; do not reintroduce WorldLoaderFolia or DefaultGroupAssigner.
