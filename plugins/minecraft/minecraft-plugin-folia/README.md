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


## Multi-action death offers

A death offer remains open until its timer expires or every remaining option is exhausted. Actions are sequential, never concurrent:

- coordinates do not resolve the items, so a chest, return, or ordinary drop may be selected afterwards;
- a denied purchase keeps the captured items and the remaining buttons available;
- ordinary drop resolves the items but leaves coordinates and return available;
- return releases unresolved items at the death point before the spectator rescue, so a chest cannot be selected afterwards;
- every paid action has its own deterministic request id and its own compensation marker, preventing duplicate charges and accidental whole-balance restoration.

Periodic link-state refreshes do not repeat the offer. A refreshed button line is sent only after an explicit action result, a real link-state transition, respawn, or reconnect.


## Config hot reload

- TaskForgeLink: `/taskforgelink reload` or `/tflink reload` (`taskforge.link.reload`, OP by default). This restarts the embedded HTTP server, backend client, chat polling, link cache schedules and death-recovery manager without restarting Minecraft.
- CustomMobTweaks: `/custommobtweaks reload` or `/cmt reload` (`custommobtweaks.admin`, OP by default). This stops old listeners/tasks, rereads `config.yml`, and reconstructs every module.

Use `/tflink status` and `/custommobtweaks modules` to inspect the current runtime after reload.
