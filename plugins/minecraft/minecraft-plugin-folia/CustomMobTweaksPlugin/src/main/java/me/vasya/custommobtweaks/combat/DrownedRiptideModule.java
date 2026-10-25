package me.vasya.custommobtweaks.combat;

import com.destroystokyo.paper.entity.ai.GoalType;
import io.papermc.paper.event.entity.EntityAttemptSpinAttackEvent;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import org.bukkit.Bukkit;
import org.bukkit.FluidCollisionMode;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.entity.Drowned;
import org.bukkit.entity.Entity;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Mob;
import org.bukkit.entity.Trident;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.event.entity.ProjectileLaunchEvent;
import org.bukkit.inventory.EquipmentSlot;
import org.bukkit.potion.PotionEffect;
import org.bukkit.potion.PotionEffectType;
import org.bukkit.util.BoundingBox;
import org.bukkit.util.Vector;

import java.util.HashSet;
import java.util.Set;
import java.util.UUID;

/** Real movement with an escapeable fixed heading; no teleports or homing during a dash. */
public final class DrownedRiptideModule extends ManagedMobModule {
    public DrownedRiptideModule(CustomMobTweaksPlugin plugin) {
        super(plugin, "drowned-riptide", 1L);
    }

    @Override protected boolean accepts(Entity entity) { return entity instanceof Drowned; }
    @Override protected Session createSession(Mob entity) { return new RiptideSession((Drowned) entity); }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void replaceThrow(ProjectileLaunchEvent event) {
        // Normal custom trident-zombies are Zombie, not Drowned, and keep their ranged module.
        if (running && event.getEntity() instanceof Trident trident && trident.getShooter() instanceof Drowned drowned
                && sessions.get(drowned.getUniqueId()) instanceof RiptideSession state && state.active) {
            event.setCancelled(true);
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void preventAutomaticSpinDamage(EntityAttemptSpinAttackEvent event) {
        if (running && sessions.get(event.getEntity().getUniqueId()) instanceof RiptideSession state
                && state.dashTicks > 0) event.setCancelled(true);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void preventDuplicateMelee(EntityDamageByEntityEvent event) {
        if (running && event.getDamager() instanceof Drowned
                && sessions.get(event.getDamager().getUniqueId()) instanceof RiptideSession state
                && state.controlsCombat && !state.applyingDamage) event.setCancelled(true);
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void glowOnDashHit(EntityDamageByEntityEvent event) {
        if (!running || event.getFinalDamage() <= 0.0 || !(event.getEntity() instanceof LivingEntity victim)
                || !(event.getDamager() instanceof Drowned)
                || !(sessions.get(event.getDamager().getUniqueId()) instanceof RiptideSession state)
                || !state.applyingDamage || state.dashTicks <= 0) return;
        int duration = Math.max(1, Math.min(72000, plugin.getConfig().getInt("drowned-trident-glow.duration-ticks", 2400)));
        int amplifier = Math.max(0, Math.min(255, plugin.getConfig().getInt("drowned-trident-glow.amplifier", 0)));
        if (plugin.getConfig().getBoolean("drowned-trident-glow.enabled", true)) {
            victim.addPotionEffect(new PotionEffect(PotionEffectType.GLOWING, duration, amplifier));
        }
        Vector push = state.heading.clone().multiply(number("hit-knockback", 0.7, 0.0, 2.0));
        push.setY(Math.max(push.getY(), 0.12));
        victim.setVelocity(victim.getVelocity().add(push));
    }

    private final class RiptideSession extends Session {
        private final Drowned drowned;
        private final Set<UUID> hit = new HashSet<>();
        private boolean controlsCombat;
        private boolean applyingDamage;
        private int windup;
        private int dashTicks;
        private long readyAt;
        private long meleeReadyAt;
        private Vector heading = new Vector();
        private Location previousCenter;
        private double travelled;

        private RiptideSession(Drowned drowned) { super(drowned); this.drowned = drowned; }

        @Override protected void initialize() {
            guard("trident_motion", () -> controlsCombat, GoalType.MOVE, GoalType.LOOK);
        }

        @Override protected void tick() {
            if (!CombatSupport.holds(drowned, Material.TRIDENT) || drowned.isInsideVehicle()) {
                if (controlsCombat || dashTicks > 0 || windup > 0) reset();
                return;
            }
            if (dashTicks > 0) { moveDash(); return; }
            LivingEntity target = drowned.getTarget();
            double range = number("attack-range", 16.0, 4.0, 32.0);
            if (!CombatSupport.usableTarget(drowned, target, range * 2.0)) {
                reset();
                return;
            }
            controlsCombat = true;
            Vector facing = target.getEyeLocation().toVector().subtract(drowned.getEyeLocation().toVector());
            if (facing.lengthSquared() > 0.001) {
                Location rotation = drowned.getLocation().setDirection(facing);
                drowned.setRotation(rotation.getYaw(), rotation.getPitch());
            }
            boolean wet = !flag("require-water-or-rain", true) || drowned.isInWaterOrRainOrBubbleColumn();
            if (windup > 0) {
                if (!wet || !drowned.hasLineOfSight(target)) { finishDash(); return; }
                drowned.getPathfinder().stopPathfinding();
                drowned.setVelocity(drowned.getVelocity().multiply(0.25));
                particles();
                if (--windup == 0) launch(target);
                return;
            }
            double distance = target.getLocation().distanceSquared(drowned.getLocation());
            double minimum = number("minimum-dash-distance", 3.0, 1.0, 8.0);
            if (wet && age >= readyAt && distance >= minimum * minimum
                    && distance <= range * range && drowned.hasLineOfSight(target)) {
                windup = ticks("windup-ticks", 10, 1, 80);
                drowned.getPathfinder().stopPathfinding();
                EquipmentSlot hand = drowned.getEquipment().getItemInMainHand().getType() == Material.TRIDENT
                        ? EquipmentSlot.HAND : EquipmentSlot.OFF_HAND;
                drowned.startUsingItem(hand);
                return;
            }
            if (age % 10L == 0L) drowned.getPathfinder().moveTo(target, 1.0);
            if (distance <= 2.3 * 2.3 && age >= meleeReadyAt && drowned.hasLineOfSight(target)) {
                meleeReadyAt = age + 20L;
                applyingDamage = true;
                try { drowned.attack(target); } finally { applyingDamage = false; }
            }
        }

        private void launch(LivingEntity target) {
            drowned.clearActiveItem();
            Location center = center();
            heading = target.getBoundingBox().getCenter().subtract(center.toVector());
            if (heading.lengthSquared() < 0.01) { finishDash(); return; }
            heading.normalize();
            previousCenter = center;
            travelled = 0.0;
            hit.clear();
            dashTicks = ticks("dash-duration-ticks", 12, 2, 40);
            drowned.setRiptiding(true);
            drowned.setVelocity(heading.clone().multiply(number("dash-speed", 1.35, 0.3, 2.5)));
            drowned.getWorld().playSound(drowned.getLocation(), Sound.ITEM_TRIDENT_RIPTIDE_1, 1.3F, 0.9F);
            debug("dash drowned=" + drowned.getUniqueId() + " target=" + target.getUniqueId());
        }

        private Location center() { return drowned.getBoundingBox().getCenter().toLocation(drowned.getWorld()); }

        private void moveDash() {
            Location current = center();
            if (previousCenter == null || !current.getWorld().equals(previousCenter.getWorld())) { finishDash(); return; }
            double step = current.distance(previousCenter);
            if (step > 5.0) { finishDash(); return; } // Do not sweep a plugin teleport as a melee hit.
            travelled += step;
            damageAlong(previousCenter, current);
            if (--dashTicks <= 0 || travelled >= number("maximum-dash-distance", 12.0, 2.0, 32.0)) { finishDash(); return; }
            double speed = number("dash-speed", 1.35, 0.3, 2.5);
            Location ahead = current.clone().add(heading.clone().multiply(speed + drowned.getWidth() * 0.5));
            if (!CombatSupport.ownsArea(current, ahead)
                    || drowned.getWorld().rayTraceBlocks(current, heading, speed + drowned.getWidth() * 0.5,
                    FluidCollisionMode.NEVER, true) != null) { finishDash(); return; }
            previousCenter = current;
            drowned.setRiptiding(true);
            drowned.setVelocity(heading.clone().multiply(speed));
            particles();
        }

        private void damageAlong(Location from, Location to) {
            Vector start = from.toVector(), end = to.toVector();
            Vector delta = end.clone().subtract(start);
            double length = delta.length();
            Location midpoint = from.clone().add(delta.clone().multiply(0.5));
            double radius = number("hit-radius", 0.55, 0.1, 1.5);
            for (LivingEntity victim : CombatSupport.nearbyOwned(midpoint, 3.0 + length * 0.5)) {
                if (victim instanceof Drowned || !CombatSupport.combatant(victim) || hit.contains(victim.getUniqueId())) continue;
                BoundingBox box = victim.getBoundingBox().clone().expand(radius);
                boolean intersects = CombatMath.segmentIntersectsBox(
                        start.getX(), start.getY(), start.getZ(), end.getX(), end.getY(), end.getZ(),
                        box.getMinX(), box.getMinY(), box.getMinZ(), box.getMaxX(), box.getMaxY(), box.getMaxZ());
                if (!intersects || !CombatSupport.lineOfSight(to, victim.getBoundingBox().getCenter().toLocation(to.getWorld()))) continue;
                hit.add(victim.getUniqueId());
                applyingDamage = true;
                try { victim.damage(number("hit-damage", 7.0, 0.0, 40.0), drowned); }
                finally { applyingDamage = false; }
            }
        }

        private void particles() {
            drowned.getWorld().spawnParticle(Particle.SPLASH, center(), 9, 0.35, 0.4, 0.35, 0.05);
            if (drowned.isInWater()) drowned.getWorld().spawnParticle(Particle.BUBBLE, center(), 6, 0.3, 0.3, 0.3, 0.02);
        }

        private void finishDash() {
            windup = 0;
            dashTicks = 0;
            previousCenter = null;
            hit.clear();
            readyAt = age + ticks("cooldown-ticks", 80, 1, 12000);
            drowned.setRiptiding(false);
            drowned.clearActiveItem();
            Vector velocity = drowned.getVelocity();
            drowned.setVelocity(new Vector(velocity.getX() * 0.25, velocity.getY() * 0.5, velocity.getZ() * 0.25));
        }

        private void reset() {
            if (windup > 0 || dashTicks > 0) finishDash();
            controlsCombat = false;
        }

        @Override protected void cleanup() {
            if (drowned.isValid()) {
                reset();
                drowned.setRiptiding(false);
                drowned.clearActiveItem();
                drowned.getPathfinder().stopPathfinding();
            }
        }
    }
}
