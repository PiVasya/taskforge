package me.vasya.custommobtweaks.combat;

import com.destroystokyo.paper.entity.ai.GoalType;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.attribute.Attribute;
import org.bukkit.attribute.AttributeModifier;
import org.bukkit.entity.Entity;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Mob;
import org.bukkit.entity.Pillager;
import org.bukkit.entity.Player;
import org.bukkit.entity.Vindicator;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.event.entity.EntityTargetLivingEntityEvent;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.util.Vector;

import java.util.UUID;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;

/** A wounded Vindicator flees; nearby real crossbow Pillagers rush to its attacker. */
public final class VindicatorAlarmModule extends ManagedMobModule {
    private final NamespacedKey alarmKey;
    private final NamespacedKey attackerKey;
    private final NamespacedKey speedKey;

    public VindicatorAlarmModule(CustomMobTweaksPlugin plugin) {
        super(plugin, "vindicator-alarm", 5L);
        alarmKey = new NamespacedKey(plugin, "vindicator_alarm_used");
        attackerKey = new NamespacedKey(plugin, "vindicator_alarm_attacker");
        // Per-runtime key prevents a delayed old cleanup from removing a new runtime's modifier.
        speedKey = new NamespacedKey(plugin, "alarm_speed_" + UUID.randomUUID().toString().replace("-", ""));
    }

    @Override protected boolean accepts(Entity entity) {
        return entity instanceof Vindicator || entity instanceof Pillager;
    }

    @Override protected Session createSession(Mob mob) {
        return mob instanceof Vindicator vindicator ? new FleeSession(vindicator) : new GuardSession((Pillager) mob);
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void rememberAttacker(EntityDamageByEntityEvent event) {
        if (!running || !(event.getEntity() instanceof Vindicator vindicator) || event.getFinalDamage() <= 0.0) return;
        Player player = CombatSupport.playerAttacker(event.getDamager());
        if (player == null) return;
        enroll(vindicator);
        vindicator.getScheduler().run(plugin, task -> {
            if (!running || !vindicator.isValid() || vindicator.isDead()) return;
            if (sessions.get(vindicator.getUniqueId()) instanceof FleeSession state) {
                state.lastAttacker = player;
                state.considerAlarm();
            }
        }, null);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void enforceTarget(EntityTargetLivingEntityEvent event) {
        if (!running) return;
        Session session = sessions.get(event.getEntity().getUniqueId());
        if (session instanceof FleeSession flee && flee.panicking) {
            // Suppress attacking while still keeping the threat in our own state.
            event.setTarget(null);
        } else if (session instanceof GuardSession guard && guard.threat != null) {
            Player player = guard.threat.player;
            if (CombatSupport.usableTarget(guard.mob, player, number("disengage-range", 96.0, 16.0, 128.0))) {
                event.setTarget(player);
            } else {
                event.setTarget(null);
            }
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void preventPanicMelee(EntityDamageByEntityEvent event) {
        if (running && event.getDamager() instanceof Vindicator
                && sessions.get(event.getDamager().getUniqueId()) instanceof FleeSession flee && flee.panicking) {
            event.setCancelled(true);
        }
    }

    /** Player state is sampled on the player scheduler; remote guards use immutable snapshots. */
    private final class Threat {
        private final Player player;
        private final AtomicBoolean pending = new AtomicBoolean();
        private final AtomicLong nextSample = new AtomicLong();
        private volatile Location location;
        private volatile boolean available = true;

        private Threat(Player player) { this.player = player; refresh(); }

        private void refresh() {
            long now = System.nanoTime();
            if (now < nextSample.get() || !pending.compareAndSet(false, true)) return;
            nextSample.set(now + 250_000_000L);
            var scheduled = player.getScheduler().run(plugin, task -> {
                try {
                    available = running && CombatSupport.combatant(player);
                    location = available ? player.getLocation().clone() : null;
                } finally {
                    pending.set(false);
                }
            }, () -> { available = false; location = null; pending.set(false); });
            if (scheduled == null) { available = false; location = null; pending.set(false); }
        }
    }

    private final class FleeSession extends Session {
        private final Vindicator vindicator;
        private Player lastAttacker;
        private Threat threat;
        private boolean alarmUsed;
        private boolean panicking;
        private long nextPath;
        private long lostSince = -1L;

        private FleeSession(Vindicator vindicator) { super(vindicator); this.vindicator = vindicator; }

        @Override protected void initialize() {
            alarmUsed = vindicator.getPersistentDataContainer().has(alarmKey, PersistentDataType.BYTE);
            guard("flee", () -> panicking, GoalType.MOVE, GoalType.LOOK);
            guard("panic_target", () -> panicking, GoalType.TARGET);
            if (alarmUsed && lowHealth()) {
                String stored = vindicator.getPersistentDataContainer().get(attackerKey, PersistentDataType.STRING);
                if (stored != null) {
                    try { lastAttacker = Bukkit.getPlayer(UUID.fromString(stored)); }
                    catch (IllegalArgumentException ignored) { }
                    if (lastAttacker != null) {
                        threat = new Threat(lastAttacker);
                        panicking = true;
                    }
                }
            }
        }

        private boolean lowHealth() {
            var maximum = vindicator.getAttribute(Attribute.MAX_HEALTH);
            return maximum != null && CombatMath.belowHealthThreshold(vindicator.getHealth(), maximum.getValue(),
                    number("health-threshold", 0.40, 0.01, 0.99));
        }

        private void considerAlarm() {
            if (!active || alarmUsed || !lowHealth()) return;
            if (lastAttacker == null && vindicator.getTarget() instanceof Player player) lastAttacker = player;
            if (lastAttacker == null) return;
            if (threat == null || threat.player != lastAttacker) threat = new Threat(lastAttacker);
            threat.refresh();
            Location attackerLocation = threat.location;
            if (!threat.available || attackerLocation == null || !attackerLocation.getWorld().equals(vindicator.getWorld())
                    || attackerLocation.distanceSquared(vindicator.getLocation()) > Math.pow(number("disengage-range", 96.0, 16.0, 128.0), 2)) return;
            alarmUsed = true;
            panicking = true;
            vindicator.getPersistentDataContainer().set(alarmKey, PersistentDataType.BYTE, (byte) 1);
            vindicator.getPersistentDataContainer().set(attackerKey, PersistentDataType.STRING, lastAttacker.getUniqueId().toString());
            vindicator.setTarget(null);
            vindicator.getPathfinder().stopPathfinding();
            Location origin = vindicator.getLocation().clone();
            origin.getWorld().playSound(origin, Sound.ENTITY_VINDICATOR_AMBIENT, 2.0F, 0.65F);
            origin.getWorld().spawnParticle(Particle.ANGRY_VILLAGER, origin.clone().add(0.0, 2.0, 0.0), 8, 0.4, 0.3, 0.4, 0.0);
            Threat signal = threat;
            double radius = number("call-radius", 48.0, 4.0, 96.0);
            debug("alarm vindicator=" + vindicator.getUniqueId() + " attacker=" + lastAttacker.getUniqueId() + " radius=" + radius);
            CombatSupport.forEachNearby(plugin, origin, radius, () -> running && signal.available, candidate -> {
                if (!(candidate instanceof Pillager pillager) || !CombatSupport.holds(pillager, Material.CROSSBOW)) return;
                enroll(pillager);
                pillager.getScheduler().run(plugin, task -> {
                    if (running && pillager.isValid() && !pillager.isDead()
                            && sessions.get(pillager.getUniqueId()) instanceof GuardSession guard) guard.protect(signal);
                }, null);
            });
        }

        @Override protected void tick() {
            if (!panicking) { considerAlarm(); return; }
            threat.refresh();
            Location position = threat.location;
            double disengage = number("disengage-range", 96.0, 16.0, 128.0);
            if (!threat.available || position == null || !position.getWorld().equals(vindicator.getWorld())
                    || position.distanceSquared(vindicator.getLocation()) > disengage * disengage) {
                if (lostSince < 0L) lostSince = age;
                if (age - lostSince >= 40L) endPanic();
                return;
            }
            lostSince = -1L;
            if (vindicator.getTarget() != null) vindicator.setTarget(null);
            if (age >= nextPath) {
                nextPath = age + 10L;
                CombatSupport.moveAway(vindicator, position, number("flee-step-blocks", 8.0, 3.0, 16.0),
                        number("flee-speed-multiplier", 1.5, 1.0, 3.0));
            }
        }

        private void endPanic() {
            panicking = false;
            threat = null;
            if (vindicator.isValid()) vindicator.getPathfinder().stopPathfinding();
        }

        @Override protected void cleanup() { endPanic(); }
    }

    private final class GuardSession extends Session {
        private final Pillager pillager;
        private Threat threat;
        private LivingEntity oldTarget;
        private long expires;
        private long nextPath;
        private boolean approaching;

        private GuardSession(Pillager pillager) { super(pillager); this.pillager = pillager; }

        @Override protected void initialize() {
            guard("alarm_target", () -> threat != null, GoalType.TARGET);
            guard("rush", () -> threat != null && approaching, GoalType.MOVE, GoalType.LOOK);
        }

        private void protect(Threat signal) {
            if (!active || !signal.available || !CombatSupport.holds(pillager, Material.CROSSBOW)) return;
            if (threat == null) oldTarget = pillager.getTarget();
            threat = signal;
            expires = age + ticks("guard-duration-ticks", 400, 20, 2400);
            var speed = pillager.getAttribute(Attribute.MOVEMENT_SPEED);
            if (speed != null) {
                speed.removeModifier(speedKey);
                speed.addTransientModifier(new AttributeModifier(speedKey,
                        number("guard-speed-multiplier", 1.8, 1.0, 4.0) - 1.0,
                        AttributeModifier.Operation.MULTIPLY_SCALAR_1));
            }
            if (CombatSupport.usableTarget(pillager, signal.player, number("disengage-range", 96.0, 16.0, 128.0))) {
                pillager.setTarget(signal.player);
            } else {
                pillager.setTarget(null);
            }
            nextPath = 0L;
            debug("guard recruited pillager=" + pillager.getUniqueId() + " attacker=" + signal.player.getUniqueId());
        }

        @Override protected void tick() {
            if (threat == null) return;
            threat.refresh();
            Location position = threat.location;
            double disengage = number("disengage-range", 96.0, 16.0, 128.0);
            if (age >= expires || !threat.available || !CombatSupport.holds(pillager, Material.CROSSBOW)
                    || (position != null && (!position.getWorld().equals(pillager.getWorld())
                    || position.distanceSquared(pillager.getLocation()) > disengage * disengage))) {
                release();
                return;
            }
            if (position == null) return;
            boolean local = CombatSupport.usableTarget(pillager, threat.player, disengage);
            if (local && pillager.getTarget() != threat.player) pillager.setTarget(threat.player);
            double distance = position.distanceSquared(pillager.getLocation());
            double shootingRange = number("shooting-distance", 12.0, 4.0, 20.0);
            boolean shouldApproach = !local || distance > shootingRange * shootingRange || !pillager.hasLineOfSight(threat.player);
            if (!shouldApproach) {
                if (approaching) pillager.getPathfinder().stopPathfinding();
                approaching = false;
                // Vanilla crossbow AI now owns movement/looking and performs the real shots.
                return;
            }
            approaching = true;
            if (age < nextPath) return;
            nextPath = age + 10L;
            if (local) {
                pillager.getPathfinder().moveTo(threat.player, 1.0);
            } else {
                // Walk towards a snapshot without touching the player from a foreign region.
                Vector delta = position.toVector().subtract(pillager.getLocation().toVector()).setY(0.0);
                if (delta.lengthSquared() > 1.0) {
                    Location step = pillager.getLocation().add(delta.normalize().multiply(8.0));
                    if (CombatSupport.ownsArea(pillager.getLocation(), step)) pillager.getPathfinder().moveTo(step, 1.0);
                }
            }
        }

        private void release() {
            Threat previous = threat;
            threat = null;
            approaching = false;
            var speed = pillager.getAttribute(Attribute.MOVEMENT_SPEED);
            if (speed != null) speed.removeModifier(speedKey);
            if (pillager.isValid()) {
                pillager.getPathfinder().stopPathfinding();
                if (previous != null && pillager.getTarget() == previous.player) {
                    pillager.setTarget(CombatSupport.usableTarget(pillager, oldTarget, 48.0) ? oldTarget : null);
                }
            }
            oldTarget = null;
        }

        @Override protected void cleanup() { release(); }
    }
}
