# TaskForge Minecraft plugins

This repository intentionally contains exactly two Minecraft plugins:

1. `TaskForgeFoliaPlugin` -> `TaskForgeLink.jar`
2. `CustomMobTweaksPlugin` -> `CustomMobTweaks.jar`

No world loader or default-group plugin is maintained here.

## Development logging policy

`debug.enabled` and `debug.deathRecovery` stay enabled so each death can be traced from capture to menu delivery and action completion. Idle heartbeat logs, periodic link-status requests, full HTTP bodies, scheduler internals and journal-write spam are disabled by default. Optional verbose diagnostics can be enabled temporarily in `config.yml`.

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

Event-driven link-status checks always send the exact UUID together with the current nickname. UUID-bound links are resolved strictly by UUID. A nickname may bind an UUID only when exactly one active UUID-less link exists for that exact nickname.


## Single-choice death offers

Each death shows one button line and accepts exactly one action. The first valid click closes the offer before any HTTP request, so double-clicks, reconnects and backend synchronization cannot create a second choice or charge.

- coordinates: charge once, show the death coordinates, then release the captured items at the death point;
- chest: charge once and create the death chest;
- return: charge once, release unresolved items at the death point, then start spectator return;
- chest + return: charge once for the bundle, create the chest, then start return;
- ordinary drop: release the captured items without a rating request.

If a paid action is denied, the menu stays closed and unresolved items fall back to the ordinary death-point drop. Existing multi-action journal/backend rows are normalized on load: any non-empty action means the choice was already consumed.

There is no periodic link-state refresh and no action-completion re-offer. The initial line is delivered only from respawn, reconnect/link recovery, hot reload recovery, or the finite per-death fallback checks.


## Config hot reload

- TaskForgeLink: `/taskforgelink reload` or `/tflink reload` (`taskforge.link.reload`, OP by default). This restarts the embedded HTTP server, backend client, chat polling, event-driven link cache and death-recovery manager without restarting Minecraft.
- CustomMobTweaks: `/custommobtweaks reload` or `/cmt reload` (`custommobtweaks.admin`, OP by default). This stops old listeners/tasks, rereads `config.yml`, and reconstructs every module.

Use `/tflink status` and `/custommobtweaks modules` to inspect the current runtime after reload.
