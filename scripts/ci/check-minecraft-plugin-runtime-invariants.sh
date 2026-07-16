#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "minecraft plugin runtime invariant failed: $*" >&2
  exit 1
}

plugins_root='plugins/minecraft'
folia_root="$plugins_root/minecraft-plugin-folia"
taskforge="$folia_root/TaskForgeFoliaPlugin/src/main/java/com/taskforge/folia/DeathRecoveryManager.java"
taskforge_main="$folia_root/TaskForgeFoliaPlugin/src/main/java/com/taskforge/folia/TaskForgeLinkPlugin.java"
taskforge_config="$folia_root/TaskForgeFoliaPlugin/src/main/resources/config.yml"
cmt="$folia_root/CustomMobTweaksPlugin/src/main/java/me/vasya/custommobtweaks/MobEffectsListener.java"
cmt_listener="$folia_root/CustomMobTweaksPlugin/src/main/java/me/vasya/custommobtweaks/ListenerComponent.java"
cmt_legacy="$folia_root/CustomMobTweaksPlugin/src/main/java/me/vasya/custommobtweaks/LegacyEnhancementsListener.java"
cmt_config="$folia_root/CustomMobTweaksPlugin/src/main/resources/config.yml"

for file in "$taskforge" "$taskforge_main" "$taskforge_config" "$cmt" "$cmt_listener" "$cmt_legacy" "$cmt_config"; do
  [ -f "$file" ] || fail "missing $file"
done

mapfile -t top_dirs < <(find "$plugins_root" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort)
expected_top=('minecraft-plugin-folia')
if [ "${top_dirs[*]}" != "${expected_top[*]}" ]; then
  fail "retired top-level Minecraft plugin projects returned; found: ${top_dirs[*]:-(none)}"
fi

mapfile -t plugin_dirs < <(find "$folia_root" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort)
expected=('CustomMobTweaksPlugin' 'TaskForgeFoliaPlugin')
if [ "${plugin_dirs[*]}" != "${expected[*]}" ]; then
  fail "maintained plugin set is not exactly TaskForgeLink + CustomMobTweaks; found: ${plugin_dirs[*]:-(none)}"
fi

grep -Fq 'PlayerRespawnEvent' "$taskforge" \
  || fail 'standard PlayerRespawnEvent death-offer trigger is missing'
grep -Fq 'PlayerPostRespawnEvent' "$taskforge" \
  || fail 'Paper/Folia PlayerPostRespawnEvent death-offer trigger is missing'
grep -Fq 'respawn processing queued' "$taskforge" \
  || fail 'respawn delivery diagnostics are missing'
grep -Fq 'offer delivered' "$taskforge" \
  || fail 'death-offer delivery diagnostic is missing'
grep -Fq 'respawn wait started' "$taskforge" \
  || fail 'local per-death respawn watcher is missing'
grep -Fq 'networkRequestsWhileWaiting=false globalPolling=false' "$taskforge" \
  || fail 'respawn watcher must stay local and network-free while waiting'
grep -Fq 'backendPullSkipped=true reason=local-offer-delivered' "$taskforge" \
  || fail 'respawn path still performs an unnecessary backend pending pull after local delivery'
grep -Fq 'saveBackendSnapshot(remote)' "$taskforge" \
  || fail 'backend pending snapshots are echoed back instead of being stored locally only'
grep -Fq 'record.actionsInFlight.remove(ACTION_DROP)' "$taskforge" \
  || fail 'ordinary drop does not finish atomically with its final DROPS_RELEASED state'
grep -Fq 'summarizeBackendResponse' "$taskforge" \
  || fail 'death backend responses may expose full item payloads'
if grep -Fq 'alive-heartbeat-fallback' "$taskforge"; then
  fail 'global alive-player death-offer polling returned'
fi
if grep -Fq 'queueInitialOfferFallback' "$taskforge"; then
  fail 'one-second death-menu polling fallback returned'
fi
if grep -Fq 'death-watchdog-1s' "$taskforge" || grep -Fq 'death-watchdog-4s' "$taskforge"; then
  fail 'fixed 1s/4s respawn watchdog returned'
fi
if grep -Eq 'private[[:space:]]+void[[:space:]]+heartbeat[[:space:]]*\(' "$taskforge"; then
  fail 'global one-second death-recovery heartbeat returned'
fi
if grep -Fq 'refreshOnlineLinkStatesSafe' "$taskforge_main"; then
  fail 'periodic online-player link-state polling returned'
fi
if grep -Fq 'periodic refresh tick' "$taskforge_main"; then
  fail 'periodic link-state refresh diagnostics returned'
fi
if grep -Fq 'linkStatusRefreshSeconds:' "$taskforge_config"; then
  fail 'periodic link-status interval returned to config.yml'
fi
grep -Fq 'deathRecovery: true' "$taskforge_config" \
  || fail 'focused death-recovery diagnostics are not enabled by default'
grep -Fq 'verboseHttp: false' "$taskforge_config" \
  || fail 'verbose HTTP logging must stay disabled by default'
grep -Fq 'verboseHttpBodies: false' "$taskforge_config" \
  || fail 'full HTTP body logging must stay disabled by default'
grep -Fq 'connectivityProbeSeconds: 0' "$taskforge_config" \
  || fail 'background connectivity probing must stay disabled by default'
grep -Fq 'MAX_MAINTENANCE_ATTEMPTS = 5' "$taskforge" \
  || fail 'death-recovery maintenance retries are not bounded'
grep -Fq 'offer consumed by single-choice selection' "$taskforge" \
  || fail 'death offer is not atomically consumed by the first selected action'
grep -Fq 'Выберите одно действие. После выбора предложение закроется.' "$taskforge" \
  || fail 'single-choice death-offer message is missing'
if grep -Fq 'Можно выполнить несколько действий' "$taskforge"; then
  fail 'multi-action death offer returned'
fi
if grep -Eq 'showOffer\(player, (record|session\.record), true, "(coordinates|chest|return|drop|action)-' "$taskforge"; then
  fail 'death menu is re-offered after a selected action'
fi
grep -Fq 'death-action-already-selected' 'services/minecraft/api/Endpoints/DeathRecovery/DeathRecoveryEndpoints.cs' \
  || fail 'backend single-choice purchase guard is missing'
grep -Fq 'record.action == null || record.action.isBlank()' "$taskforge" \
  || fail 'selected death action can still be considered an open offer'
if grep -Fq 'incomingAction == "drop"' 'services/minecraft/api/Endpoints/DeathRecovery/DeathRecoveryEndpoints.cs'; then
  fail 'backend still allows rewriting the selected death action to drop'
fi

grep -Fq 'damage-cancelled-or-shielded' "$cmt" \
  || fail 'shield/cancelled damage does not stop sniper homing'
grep -Fq 'stopArrowHoming(arrow, "projectile-hit")' "$cmt" \
  || fail 'projectile impact does not stop sniper homing'
grep -Fq 'implements Listener, PluginComponent' "$cmt" \
  || fail 'sniper homing tasks are not reload-managed'
grep -Fq 'component.shutdown();' "$cmt_listener" \
  || fail 'listener-owned tasks are not cancelled during reload'

if grep -RIEq 'RadiationManager|radiation-zone|createZone\(' "$folia_root/CustomMobTweaksPlugin/src/main"; then
  fail 'radioactive-zone runtime or configuration returned'
fi
if grep -Fq 'guaranteedBreezeElytra' "$cmt_legacy"; then
  fail 'Breeze Elytra was forced back to a guaranteed drop'
fi
grep -Fq 'configuredChance = drop.getDouble("chance", 0.0D)' "$cmt_legacy" \
  || fail 'extra-loot chance is not read from configuration'
grep -Fq 'effectiveChance=' "$cmt_legacy" \
  || fail 'extra-loot roll diagnostics do not expose the effective chance'
if ! grep -A4 -F '      elytra:' "$cmt_config" | grep -Fq 'chance: 0.05'; then
  fail 'default Breeze Elytra chance is not 5%'
fi
grep -Fq 'rememberLootAttribution' "$cmt_legacy" \
  || fail 'indirect player kill attribution for extra loot is missing'
grep -Fq 'result=DROP' "$cmt_legacy" \
  || fail 'event-driven extra-loot diagnostics are missing'

grep -Fq 'return skeleton.getType() == EntityType.SKELETON;' "$cmt" \
  || fail 'skeleton sniper is not restricted to the normal vanilla Skeleton'
if grep -Eq 'skeleton-sniper\.include-(strays|bogged)' "$cmt"; then
  fail 'legacy sniper variant switches can still enable Stray or Bogged modifications'
fi
if grep -Eq '^[[:space:]]+include-(strays|bogged):' "$cmt_config"; then
  fail 'legacy sniper variant switches remain in the bundled config'
fi

grep -Fq 'homing-turn-rate: 0.90' "$cmt_config" \
  || fail 'expected slightly reduced homing turn rate is missing'
grep -Fq 'homing-lead-factor: 0.95' "$cmt_config" \
  || fail 'expected slightly reduced homing lead is missing'

echo 'minecraft plugin runtime invariants ok'
