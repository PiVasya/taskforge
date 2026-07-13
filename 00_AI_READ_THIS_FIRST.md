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

## Minecraft death-recovery safety policy

- Never clear `PlayerDeathEvent#getDrops()` unless the current online-session cache has an authoritative `LINKED` result for that exact UUID.
- `UNLINKED` and `UNKNOWN` players must keep fully vanilla death behavior: do not serialize items, do not create a death-recovery journal row, do not create a backend row, do not alter experience, and do not delay drops.
- After an unlinked death, show a concise message after respawn explaining that linking `taskforge.by` enables coordinates, death chests, and return-to-death actions.
- If link status cannot be verified, prefer vanilla drops over interception. A previously confirmed linked state may be preserved during a transient refresh failure for the current online session.
- The backend must reject creation of a new death-recovery row for an unlinked player and must return an explicit `not-linked` reason.
- Keep verbose diagnostics for link-cache transitions, death gating, vanilla-drop preservation, backend rejection, scheduler delivery, and recovery state transitions until the user explicitly changes the logging policy.
