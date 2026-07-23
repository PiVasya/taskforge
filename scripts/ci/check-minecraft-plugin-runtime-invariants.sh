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
cmt_clones="$folia_root/CustomMobTweaksPlugin/src/main/java/me/vasya/custommobtweaks/IllusionerCloneManager.java"
cmt_dragon_root="$folia_root/CustomMobTweaksPlugin/src/main/java/me/vasya/custommobtweaks/dragon"
cmt_dragon="$cmt_dragon_root/EnderDragonRework.java"
cmt_dragon_breath="$cmt_dragon_root/DragonBreathAttack.java"
cmt_dragonlings="$cmt_dragon_root/DragonlingManager.java"
cmt_dragonling_ai="$cmt_dragon_root/DragonlingAttackController.java"
cmt_main="$folia_root/CustomMobTweaksPlugin/src/main/java/me/vasya/custommobtweaks/CustomMobTweaksPlugin.java"
cmt_config="$folia_root/CustomMobTweaksPlugin/src/main/resources/config.yml"

for file in "$taskforge" "$taskforge_main" "$taskforge_config" "$cmt" "$cmt_listener" "$cmt_legacy" "$cmt_clones" "$cmt_dragon" "$cmt_dragon_breath" "$cmt_dragonlings" "$cmt_dragonling_ai" "$cmt_main" "$cmt_config"; do
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

grep -Fq 'EntityShootBowEvent' "$cmt_clones" \
  || fail 'Illusioner clone creation is not tied to bow shots'
grep -Fq 'max-active-per-original", 30' "$cmt_clones" \
  || fail 'Illusioner clone family is not capped at 30 by default'
grep -Fq 'max-generation", 7' "$cmt_clones" \
  || fail 'Illusioner clone generation cap is missing'
grep -Fq 'minimum-lifetime-ticks", 60L' "$cmt_clones" \
  || fail 'Illusioner clone lifetime can fall below three seconds'
grep -Fq 'lifetime-reduction-per-generation-ticks", 40L' "$cmt_clones" \
  || fail 'Illusioner clone lifetimes do not decrease by generation'
grep -Fq 'clone.getScheduler().runDelayed' "$cmt_clones" \
  || fail 'Illusioner clones are not retired on their Folia entity scheduler'
grep -Fq 'Bukkit.getRegionScheduler().execute' "$cmt_clones" \
  || fail 'Illusioner clone spawning is not dispatched to the owning Folia region'
grep -Fq 'pending=' "$cmt_clones" \
  || fail 'pending Illusioner clone spawns are not accounted for when the root dies'
grep -Fq 'illusioner_clone_generation' "$cmt_clones" \
  || fail 'Illusioner clone generation is not persisted in PDC'
grep -Fq 'event.getDrops().clear();' "$cmt_clones" \
  || fail 'Illusioner clones may drop vanilla loot'
grep -Fq 'entity.getPersistentDataContainer().has(illusionerCloneKey' "$cmt_legacy" \
  || fail 'Illusioner clones may receive configured extra loot'
if grep -Eq 'runAtFixedRate|GlobalRegionScheduler' "$cmt_clones"; then
  fail 'Illusioner clone runtime introduced global polling'
fi
if ! grep -A8 -F 'illusioner-clones:' "$cmt_config" | grep -Fq 'max-active-per-original: 30'; then
  fail 'bundled Illusioner clone cap is not 30'
fi
if ! grep -A20 -F 'illusioner-clones:' "$cmt_config" | grep -Fq 'minimum-lifetime-ticks: 60'; then
  fail 'bundled Illusioner clone minimum lifetime is not three seconds'
fi

grep -Fq 'registerComponent(new EnderDragonRework(this));' "$cmt_main"   || fail 'Ender Dragon rework is not part of the CustomMobTweaks component lifecycle'
grep -Fq 'EnderDragonShootFireballEvent' "$cmt_dragon"   || fail 'primary Ender Dragon fireball replacement event is missing'
grep -Fq 'EnderDragonFireballHitEvent' "$cmt_dragon"   || fail 'dragonling fireball cloud cancellation guard is missing'
grep -Fq 'EnderDragonFlameEvent' "$cmt_dragon"   || fail 'dragonling perched flame cancellation guard is missing'
grep -Fq 'event.setCancelled(true);' "$cmt_dragon"   || fail 'vanilla dragon fireball is not cancelled before the stream starts'
grep -Fq 'Particle.FLAME' "$cmt_dragon_breath"   || fail 'orange fire particles are missing from the dragon stream'
grep -Fq 'Particle.DUST_COLOR_TRANSITION' "$cmt_dragon_breath"   || fail 'purple fire particles are missing from the dragon stream'
grep -Fq 'Particle.DRAGON_BREATH' "$cmt_dragon_breath"   || fail 'vanilla dragon-breath particles are missing from the dragon stream'
grep -Fq 'Particle.SMOKE' "$cmt_dragon_breath"   || fail 'edge smoke is missing from the dragon stream'
grep -Fq 'Bukkit.getRegionScheduler().execute' "$cmt_dragon_breath"   || fail 'dragon stream rendering is not dispatched to Folia region schedulers'
grep -Fq 'player.getScheduler().run' "$cmt_dragon_breath"   || fail 'dragon stream player damage is not dispatched to player entity schedulers'
grep -Fq 'duration-ticks", 140L' "$cmt_dragon_breath"   || fail 'dragon fire stream is not a sustained seven-second attack by default'
grep -Fq 'block.setType(Material.FIRE, true)' "$cmt_dragon_breath"   || fail 'dragon fire stream does not place real vanilla fire blocks'
grep -Fq 'AreaEffectCloud.class' "$cmt_dragon_breath"   || fail 'dragon fire stream does not create real dragon-breath clouds'
grep -Fq '[dragon][fire]' "$cmt_dragon_breath"   || fail 'dragon fire diagnostics are missing'
grep -Fq 'landing cycle start' "$cmt_dragonlings"   || fail 'dragon landing diagnostics are missing'
grep -Fq 'dragonling spawned' "$cmt_dragonlings"   || fail 'dragonling spawn diagnostics are missing'
grep -Fq 'new NamespacedKey("taskforge", "dragonling")' "$cmt_dragonlings"   || fail 'taskforge:dragonling PDC marker is missing'
grep -Fq 'new NamespacedKey("taskforge", "dragonling_owner")' "$cmt_dragonlings"   || fail 'taskforge:dragonling_owner PDC marker is missing'
if ! grep -Fq 'EntityType.ENDER_DRAGON' "$cmt_dragonlings" \
    && ! grep -Fq 'EnderDragon.class' "$cmt_dragonlings"; then
  fail 'dragonlings are not real EnderDragon entities'
fi
grep -Fq 'Attribute.SCALE' "$cmt_dragonlings"   || fail 'dragonling server-side scale attribute is missing'
grep -Fq 'EnderDragon.Phase.CHARGE_PLAYER' "$cmt_dragonling_ai"   || fail 'dragonling charge controller is missing'
grep -Fq 'dragon.getTrackedBy()' "$cmt_dragonling_ai"   || fail 'dragonling target selection does not stay local to tracking players'
grep -Fq 'candidate.getScheduler().run' "$cmt_dragonling_ai"   || fail 'dragonling candidate validation is not dispatched to player entity schedulers'
if grep -RIEq 'Bukkit\.getScheduler\(|GlobalRegionScheduler|ItemDisplay|EntityType\.PHANTOM|taskforge:dragonling' "$cmt_dragon_root"; then
  fail 'dragon rework contains a global Bukkit scheduler, display/phantom carrier, or string-only PDC shortcut'
fi
if ! grep -A120 -F 'ender-dragon-rework:' "$cmt_config" | grep -Fq 'duration-ticks: 140'; then
  fail 'default dragon fire stream duration is not seven seconds'
fi
if ! grep -A120 -F 'ender-dragon-rework:' "$cmt_config" | grep -Fq 'place-fire: true'; then
  fail 'real dragon ground fire is not enabled by default'
fi
if ! grep -A120 -F 'ender-dragon-rework:' "$cmt_config" | grep -Fq 'create-dragon-breath-clouds: true'; then
  fail 'dragon-breath ground clouds are not enabled by default'
fi
if ! grep -A120 -F 'ender-dragon-rework:' "$cmt_config" | grep -Fq 'scale: 0.25'; then
  fail 'default dragonling scale is not 0.25'
fi
if ! grep -A120 -F 'ender-dragon-rework:' "$cmt_config" | grep -Fq 'health: 40.0'; then
  fail 'default dragonling health is not 40.0'
fi
if ! grep -A120 -F 'ender-dragon-rework:' "$cmt_config" | grep -Fq 'maximum-active: 2'; then
  fail 'default maximum active dragonlings is not 2'
fi
if ! grep -A120 -F 'ender-dragon-rework:' "$cmt_config" | grep -Fq 'charge-cooldown-min-ticks: 40'; then
  fail 'default minimum dragonling charge cooldown is not 40 ticks'
fi
if ! grep -A120 -F 'ender-dragon-rework:' "$cmt_config" | grep -Fq 'charge-cooldown-max-ticks: 80'; then
  fail 'default maximum dragonling charge cooldown is not 80 ticks'
fi

echo 'minecraft plugin runtime invariants ok'
