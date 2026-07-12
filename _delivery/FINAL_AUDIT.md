# TaskForge Minecraft mega update — final audit

- UTC: 2026-07-12T18:53:01.914750+00:00
- Status: **NEEDS REVIEW**
- Changed: 2 files (2 added, 0 modified, 0 deleted)

## Protected files
- PASS: migrations/snapshots/Worlds/Portals/bukkit data are byte-identical to the supplied archive.

## Feature scan
- [x] PlayerDeathEvent hook
- [ ] death drops intercepted
- [x] Folia region scheduler
- [x] Folia entity scheduler
- [x] exact world UUID
- [x] exact NamespacedKey
- [x] spectator rescue
- [x] offer/rescue countdown
- [x] radius 12
- [ ] chest search 16
- [x] 50/100/150 costs
- [x] plugin version 1.3.0
- [x] backend death persistence
- [x] request idempotency
- [x] compensation/refund path

## Forbidden references
- EnsureCreated outside migrations: 13
  - `services/ai/api/Program.cs`
  - `services/content/api/Program.cs`
  - `services/education/api/Program.cs`
  - `services/execution/api/Program.cs`
  - `services/files/api/Program.cs`
  - `services/identity/api/Program.cs`
  - `services/minecraft/api/Program.cs`
  - `services/notifications/api/Program.cs`
  - `services/observability/api/Program.cs`
  - `services/solutions/api/Program.cs`
  - `services/support/api/Program.cs`
  - `services/tasks/assignment-api/Program.cs`
  - `services/tasks/quiz-api/Program.cs`
- MinecraftSchemaCompatibility outside migrations: 1
  - `FINAL_AUDIT.md`
- weekly economy leftovers outside migrations: 6
  - `FINAL_AUDIT.md`
  - `services/minecraft/api/Data/MinecraftDbContext.cs`
  - `services/minecraft/api/Domain/MinecraftEconomySettings.cs`
  - `services/minecraft/api/Domain/MinecraftWeeklyJoin.cs`
  - `services/minecraft/api/Services/Common/MinecraftApiCommonService.cs`
  - `tools/database-splitter/table-ownership-map.json`
- first-world fallback: 0

## Build/test results
- `dotnet-toolchain` — TOOLCHAIN UNAVAILABLE
- `maven:plugins/minecraft/default-group-assigner` — TOOLCHAIN UNAVAILABLE
- `maven:plugins/minecraft/world-loader-folia` — TOOLCHAIN UNAVAILABLE
- `gradle:plugins/minecraft/minecraft-plugin-folia/CustomMobTweaksPlugin` — TOOLCHAIN UNAVAILABLE
- `gradle:plugins/minecraft/minecraft-plugin-folia/TaskForgeFoliaPlugin` — TOOLCHAIN UNAVAILABLE

## Changed files
- `FINAL_AUDIT.md`
- `WORK_REPORT.md`

## Runtime validation note
- EF migration generation is intentionally not part of this archive.
- Final end-to-end validation requires the real Folia server, its 30 registered Worlds entries, Portals data, and a generated EF migration.
