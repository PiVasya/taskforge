#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "minecraft plugin runtime invariant failed: $*" >&2
  exit 1
}

plugins_root='plugins/minecraft'
folia_root="$plugins_root/minecraft-plugin-folia"
taskforge="$folia_root/TaskForgeFoliaPlugin/src/main/java/com/taskforge/folia/DeathRecoveryManager.java"
cmt="$folia_root/CustomMobTweaksPlugin/src/main/java/me/vasya/custommobtweaks/MobEffectsListener.java"
cmt_listener="$folia_root/CustomMobTweaksPlugin/src/main/java/me/vasya/custommobtweaks/ListenerComponent.java"
cmt_config="$folia_root/CustomMobTweaksPlugin/src/main/resources/config.yml"

for file in "$taskforge" "$cmt" "$cmt_listener" "$cmt_config"; do
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

grep -Fq 'PlayerPostRespawnEvent' "$taskforge" \
  || fail 'death offer is not driven by PlayerPostRespawnEvent'
grep -Fq 'post-respawn-event' "$taskforge" \
  || fail 'post-respawn delivery diagnostic is missing'
if grep -Fq 'alive-heartbeat-fallback' "$taskforge"; then
  fail 'global alive-player death-offer polling returned'
fi
if grep -Fq 'queueInitialOfferFallback' "$taskforge"; then
  fail 'one-second death-menu polling fallback returned'
fi

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
