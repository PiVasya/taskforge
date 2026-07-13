# TaskForge Minecraft plugins

This repository intentionally contains exactly two Minecraft plugins:

1. `TaskForgeFoliaPlugin` -> `TaskForgeLink.jar`
2. `CustomMobTweaksPlugin` -> `CustomMobTweaks.jar`

No world loader or default-group plugin is maintained here.

## Development logging policy

During development, verbose diagnostics are mandatory. Do not disable `debug.enabled`, HTTP, death-recovery, scheduler, journal, heartbeat, or link-cache logging unless the user explicitly requests a logging-policy change.

## Death recovery safety

TaskForge death recovery intercepts drops only when the current online-session cache has an authoritative `LINKED` result from the TaskForge API.

- `LINKED`: TaskForge death offer is enabled.
- `UNLINKED`: Minecraft handles drops and experience normally; the player receives a link-account hint after respawn.
- `UNKNOWN`: Minecraft handles the death normally because the plugin could not safely confirm the link.

The backend also rejects creation of a new death-recovery row for an unlinked UUID.

## Build

```bash
cd plugins/minecraft/minecraft-plugin-folia/TaskForgeFoliaPlugin
gradle clean build

cd ../CustomMobTweaksPlugin
gradle clean build
```

## Link identity synchronization

The online status probe always sends the exact UUID together with the current nickname. UUID-bound links are resolved strictly by UUID. A nickname may bind an UUID only when exactly one active UUID-less link exists for that exact nickname.
