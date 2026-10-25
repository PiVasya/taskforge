package me.vasya.custommobtweaks.combat;

import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.entity.AbstractArrow;
import org.bukkit.entity.Entity;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Mob;
import org.bukkit.entity.Parched;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.entity.EntityShootBowEvent;
import org.bukkit.event.entity.ProjectileHitEvent;
import org.bukkit.persistence.PersistentDataType;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.UUID;

public final class ParchedSandModule extends ManagedMobModule {
    private final NamespacedKey sandArrowKey;
    private final NamespacedKey spentArrowKey;
    private final NamespacedKey readyAtKey;
    private final NamespacedKey readyWorldKey;

    public ParchedSandModule(CustomMobTweaksPlugin plugin) {
        super(plugin, "parched-sand", 2L);
        sandArrowKey = new NamespacedKey(plugin, "parched_sand_arrow");
        spentArrowKey = new NamespacedKey(plugin, "parched_sand_spent");
        readyAtKey = new NamespacedKey(plugin, "parched_teleport_ready_at");
        readyWorldKey = new NamespacedKey(plugin, "parched_teleport_world");
    }

    @Override protected boolean accepts(Entity entity) { return entity instanceof Parched; }
    @Override protected Session createSession(Mob entity) { return new SandSession((Parched) entity); }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void tagArrow(EntityShootBowEvent event) {
        if (running && event.getEntity() instanceof Parched && event.getProjectile() instanceof AbstractArrow arrow) {
            arrow.getPersistentDataContainer().set(sandArrowKey, PersistentDataType.BYTE, (byte) 1);
        }
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onImpact(ProjectileHitEvent event) {
        if (!running || !(event.getEntity() instanceof AbstractArrow arrow)
                || !arrow.getPersistentDataContainer().has(sandArrowKey, PersistentDataType.BYTE)
                || arrow.getPersistentDataContainer().has(spentArrowKey, PersistentDataType.BYTE)) return;
        arrow.getPersistentDataContainer().set(spentArrowKey, PersistentDataType.BYTE, (byte) 1);
        Location impact = arrow.getLocation().clone();
        Entity hit = event.getHitEntity();
        UUID directVictim = hit == null ? null : hit.getUniqueId();
        if (hit instanceof LivingEntity living && Bukkit.isOwnedByCurrentRegion(living)) {
            impact = living.getLocation().clone().add(0.0, Math.min(1.0, living.getHeight() * 0.5), 0.0);
        }
        double radius = number("splash-radius", 2.5, 0.5, 6.0);
        sand(impact, ticks("splash-particles", 60, 0, 250), radius * 0.5);
        impact.getWorld().playSound(impact, Sound.BLOCK_SAND_BREAK, 0.9F, 0.7F);
        final Location center = impact.clone();
        final UUID shooterId = arrow.getShooter() instanceof Entity shooter ? shooter.getUniqueId() : null;
        double damage = number("splash-damage", 1.0, 0.0, 10.0);
        if (damage <= 0.0) return;
        // The direct victim keeps exactly the vanilla arrow damage; no second hit or copied debuff.
        for (LivingEntity nearby : CombatSupport.nearbyOwned(center, radius)) {
            if (!CombatSupport.combatant(nearby) || nearby instanceof Parched
                    || nearby.getUniqueId().equals(directVictim) || nearby.getUniqueId().equals(shooterId)) continue;
            if (!CombatSupport.lineOfSight(center, nearby.getBoundingBox().getCenter().toLocation(center.getWorld()))) continue;
            nearby.damage(damage, arrow);
        }
        debug("sand impact arrow=" + arrow.getUniqueId() + " directVictim=" + directVictim + " radius=" + radius);
    }

    private void sand(Location location, int count, double spread) {
        if (count > 0) location.getWorld().spawnParticle(Particle.BLOCK, location, count,
                spread, 0.65, spread, 0.0, Material.SAND.createBlockData());
    }

    private final class SandSession extends Session {
        private final Parched parched;
        private boolean teleportPending;
        private long retryAt;

        private SandSession(Parched parched) { super(parched); this.parched = parched; }

        @Override protected void tick() {
            if (teleportPending || age < retryAt || parched.isInsideVehicle() || !parched.getPassengers().isEmpty()) return;
            long now = parched.getWorld().getGameTime();
            String worldId = parched.getWorld().getUID().toString();
            String cooldownWorld = parched.getPersistentDataContainer().get(readyWorldKey, PersistentDataType.STRING);
            long readyAt = worldId.equals(cooldownWorld)
                    ? parched.getPersistentDataContainer().getOrDefault(readyAtKey, PersistentDataType.LONG, 0L) : 0L;
            if (now < readyAt) return;
            Player threat = CombatSupport.nearestPlayer(parched, number("trigger-radius", 3.0, 1.0, 8.0));
            if (threat == null) return;
            Location origin = parched.getLocation().clone();
            int radius = ticks("teleport-radius-blocks", 1, 1, 6);
            List<Location> candidates = new ArrayList<>();
            for (int dx = -radius; dx <= radius; dx++) for (int dz = -radius; dz <= radius; dz++) {
                if (dx == 0 && dz == 0) continue;
                for (int dy : new int[] {0, 1, -1}) {
                    candidates.add(new Location(origin.getWorld(), origin.getBlockX() + dx + 0.5,
                            origin.getBlockY() + dy, origin.getBlockZ() + dz + 0.5, origin.getYaw(), origin.getPitch()));
                }
            }
            Collections.shuffle(candidates);
            Location destination = candidates.stream().filter(candidate -> CombatSupport.safeFeet(parched, candidate))
                    .findFirst().orElse(null);
            if (destination == null) {
                // A sealed box is not a reason to suffocate the mob or scan every tick forever.
                retryAt = age + 20L;
                return;
            }
            teleportPending = true;
            sand(origin.clone().add(0.0, parched.getHeight() * 0.5, 0.0), ticks("teleport-particles", 70, 0, 250), 0.3);
            parched.getWorld().playSound(origin, Sound.BLOCK_SAND_BREAK, 1.0F, 0.7F);
            int cooldown = ticks("teleport-cooldown-ticks", 140, 1, 12000);
            // Reserve before dispatch; success commits it, cancellation releases it.
            parched.getPersistentDataContainer().set(readyAtKey, PersistentDataType.LONG, now + cooldown);
            parched.getPersistentDataContainer().set(readyWorldKey, PersistentDataType.STRING, worldId);
            parched.teleportAsync(destination).whenComplete((success, error) -> {
                if (!running || !active) return;
                parched.getScheduler().run(plugin, task -> {
                    if (!running || !active || !parched.isValid()) return;
                    teleportPending = false;
                    if (error != null || !Boolean.TRUE.equals(success)) {
                        parched.getPersistentDataContainer().remove(readyAtKey);
                        parched.getPersistentDataContainer().remove(readyWorldKey);
                        retryAt = age + 20L;
                        debug("teleport rejected parched=" + parched.getUniqueId()
                                + " error=" + (error == null ? "cancelled" : error.getClass().getSimpleName()));
                        return;
                    }
                    parched.setFallDistance(0.0F);
                    sand(parched.getLocation().add(0.0, 0.9, 0.0), 35, 0.25);
                    debug("teleport parched=" + parched.getUniqueId() + " cooldownTicks=" + cooldown);
                }, null);
            });
        }
    }
}
