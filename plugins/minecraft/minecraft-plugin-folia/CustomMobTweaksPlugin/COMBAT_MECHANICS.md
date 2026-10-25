# Combat modules in CustomMobTweaks 2.8.0

Target: Folia 26.1.2, Paper API 26.1.2.build.74-stable, Java 25.
Only CustomMobTweaks is changed. TaskForgeLink, backend, frontend, database
migrations and the existing trident-zombie implementation are not changed.
Piglins and Piglin Brutes are not enrolled by these modules.

## Drowned with a trident

`drowned-riptide` replaces trident throws with a fixed-heading Riptide-style
charge. It applies only to Drowned holding a trident, not the separate custom
trident-zombie. A charge requires water/rain by default. On dry land the mob
uses ordinary melee instead of throwing.

The defaults are a 16-block attack range, 10-tick warning, 12-tick charge,
12-block maximum travel, 80-tick cooldown, 7 health points of impact damage
and 0.7 knockback. Every entity is damaged at most once per dash. Movement
segments are tested against expanded target bounding boxes so a fast mob
cannot skip a target between ticks. Walls and non-owned/unloaded terrain stop
the dash. Its direction is fixed when it launches; it is not homing.

The existing `drowned-trident-glow` settings apply to successful dash hits.
Canceled or fully blocked damage does not apply glow or bonus knockback.
The module does not alter the trident item or grant extra enchantment drops.

## Parched: neighboring-cell teleport and sand splash

`parched-sand` watches nearby Survival/Adventure players. At a distance of
3 blocks or less, a ready Parched disappears into sand particles and moves
to a random safe neighboring cell. The default horizontal radius is exactly
**one block**, with height differences of at most one block. The cooldown is
140 ticks / 7 seconds, and its deadline survives config reload and chunk unload.
There is no restriction to sand surfaces.

The destination must be loaded, owned by the current region, within the world
border, have solid support, fit the mob's complete current bounding box and
contain no liquid, fire or other listed hazard. If no safe cell exists, it
stays where it is and retries later. Blocks are never replaced or broken.
Teleportation is asynchronous and never awaited with `join()` or `get()`.

Arrows keep their original damage and potion effects on the direct victim.
Their first non-canceled impact makes a sand burst. Other nearby living
combatants within 2.5 blocks receive 1 health point of splash damage, subject
to normal armor/damage events and line of sight. The direct victim, shooter
and other Parched are excluded from that extra damage. A piercing arrow can
create only one burst, and an arrow hitting a block can create a burst too.

## Illusioner: enemies of every creature type, except its own

`illusioner-aggro` replaces only the target selector. The existing real bow
attacks, spells and recursive clone system remain in use, so clone creation
continues to receive real `EntityShootBowEvent` events.

Every valid living combatant is eligible, including zombies, skeletons,
Endermen, dragons, other hostile creatures, animals, villagers and other
illagers. The default search range is 32 blocks in the same world, with a
line-of-sight check. A valid current target is retained rather than replaced
every tick. Invisible-to-the-mob targets are remembered for 100 ticks.
Armor stands, invulnerable entities and Creative/Spectator players are excluded.

All Illusioners and all their copies are allies by default. Both target
selection and direct/projectile friendly fire are protected; arrows pass
through allied Illusioners. The projectile stores its family ID so protection
still works after its shooter dies. Set `protect-all-illusioners: false` only
to allow different root families to fight; an original and its own recursive
copies remain allies.

**Aggro is not an immunity bypass.** Endermen retain their normal projectile
avoidance and the dragon retains its phase-specific damage rules. Dragon
shots are aimed at a real body part instead of the multipart origin.
Only currently owned, loaded entities are inspected; a target on the far side
of a Folia ownership boundary is considered when the regions can safely
interact. The plugin does not read a foreign entity's position to force aggro.

## Wounded Vindicator and defending Pillagers

`vindicator-alarm` triggers once per Vindicator, strictly below 40% of its
actual maximum health. The player who damaged it is remembered, including
player-shot projectiles. Damage cancellation, absorption and lethal hits are
handled by checking the actual living mob on the following entity tick.
A low-health mob already targeting a player can also trigger its alarm.

The Vindicator keeps its axe, stops attacking and uses walkable escape paths
at speed multiplier 1.5. It does **not** become a crossbow shooter or get
replaced by another entity. Its alarm marker is persistent, so reloads do
not let it call repeatedly. Fleeing ends after the attacker leaves/dies or
moves beyond the disengagement range; healing does not re-arm the alarm.

Every currently nearby Pillager actually holding a crossbow within 48 blocks
is recruited. Existing targets are overridden. It gets a non-stacking,
transient movement modifier of x1.8 and rushes to a 12-block firing distance,
then its normal crossbow AI takes over. There is no synthetic projectile
stream, rapid-fire enchantment, new guard spawning or Piglin modification.
The default alert duration is 400 ticks / 20 seconds, with a 96-block
maximum chase distance. An expired alert restores the previous valid target
and removes only this module's speed modifier.

The one-off recruitment scan dispatches remote chunks to their owning region.
The attacker is sampled on the player scheduler. A guard in a foreign region
uses location snapshots for approach and sets the live target only after
ownership allows it. No global world-wide polling task is introduced.

## Lifecycle and configuration

All four modules appear in `/cmt modules` and `/cmt status`. They are registered
as regular reload-managed plugin components. Startup and reload inspect
already loaded chunks; subsequent spawning and chunk loading enroll mobs
through events. Entity schedulers follow mobs across region changes. Entity
unload, death and component shutdown stop controllers and remove their custom
AI goals. Vanilla AI goals are not deleted. Ordinary gameplay outside the
active mechanic is left to Minecraft.

`/cmt reload` applies settings and restarts components, but it cannot load a
new JAR. Install the newly built JAR with a server restart first. The bundled
config supplies defaults for absent keys in an existing config. Existing
explicit values are not overwritten. To tune the new behavior, copy the four
new sections into `plugins/CustomMobTweaks/config.yml` or use the accompanying
merged config, which preserves the separately supplied user values.

Each new section has `enabled` and `debug`. Diagnostics log transitions
(target changes, alarm recruitment, dash starts and teleport results), not
idle entity ticks. Existing logging defaults are not disabled.

## Automated checks

From this component directory:

```bash
gradle --no-daemon clean build
```

This compiles against the real pinned Paper API using Java 25 and runs
`combatRulesTest` as part of `check`. The separate `combatTest` source set
avoids depending on a test server or an external mocking library.

The pure threshold/config/segment-collision rules can also be exercised with
an installed JDK independently of Gradle and Paper:

```bash
bash scripts/test-combat-rules.sh
```

These checks do not simulate a Minecraft server. They do not replace a real
Folia integration test. The repository's existing `minecraft plugin jars`
workflow uses Java 25 / Gradle 9.4.1 and runs the full build.

## Folia smoke test checklist

Use a disposable test world, Survival mode and enabled debug logging. Confirm
`/cmt modules` lists all four modules as ON. Then test:

1. A trident Drowned in water: visible charge warning, movement instead of
   thrown tridents, a miss after sidestepping, no duplicate hit and no damage
   through walls. Repeat on dry ground and in rain. Verify the separate custom
   trident-zombie still throws.
2. A Parched with eight surrounding safe cells: approaching triggers a sand
   teleport, a second approach before seven seconds does not. Repeat in a
   sealed box, near water/lava, next to a wall and with a scaled-up mob. Verify
   direct arrow damage is not doubled and nearby targets get only weak splash.
3. An Illusioner with a zombie, cow, Enderman, other Illager and dragon in
   separate tests: each can be acquired. Verify copies still spawn on shots,
   share no friendly damage and respect the existing generation/count limits.
   Enderman avoidance and dragon immunity phases remain vanilla.
4. A Vindicator with nearby crossbow Pillagers: at exactly 40% there is no
   alarm; below it, the Vindicator flees and all nearby crossbow guards switch
   to the attacking player. Verify shields/armor, speed expiry, no bonus
   stacking, target restoration and no response from Piglins or unarmed guards.
5. Repeat while using `/cmt reload`, disabling each module, unloading/reloading
   chunks, crossing Folia region boundaries, leaving the server and changing
   worlds. Check for region-ownership errors and leftover speed/AI modifiers.
