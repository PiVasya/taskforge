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
cmt_config="$folia_root/CustomMobTweaksPlugin/src/main/resources/config.yml"

for file in "$taskforge" "$taskforge_main" "$taskforge_config" "$cmt" "$cmt_listener" "$cmt_config"; do
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
grep -Fq 'bounded respawn watchdogs scheduled' "$taskforge" \
  || fail 'finite per-death respawn fallback is missing'
grep -Fq 'summarizeBackendResponse' "$taskforge" \
  || fail 'death backend responses may expose full item payloads'
if grep -Fq 'alive-heartbeat-fallback' "$taskforge"; then
  fail 'global alive-player death-offer polling returned'
fi
if grep -Fq 'queueInitialOfferFallback' "$taskforge"; then
  fail 'one-second death-menu polling fallback returned'
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

grep -Fq 'damage-cancelled-or-shielded' "$cmt" \
  || fail 'shield/cancelled damage does not stop sniper homing'
grep -Fq 'stopArrowHoming(arrow, "projectile-hit")' "$cmt" \
  || fail 'projectile impact does not stop sniper homing'
grep -Fq 'implements Listener, PluginComponent' "$cmt" \
  || fail 'sniper homing tasks are not reload-managed'
grep -Fq 'component.shutdown();' "$cmt_listener" \
  || fail 'listener-owned tasks are not cancelled during reload'

grep -Fq 'homing-turn-rate: 0.90' "$cmt_config" \
  || fail 'expected slightly reduced homing turn rate is missing'
grep -Fq 'homing-lead-factor: 0.95' "$cmt_config" \
  || fail 'expected slightly reduced homing lead is missing'

echo 'minecraft plugin runtime invariants ok'
