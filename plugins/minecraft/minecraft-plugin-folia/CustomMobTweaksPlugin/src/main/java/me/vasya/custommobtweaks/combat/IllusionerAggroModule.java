package me.vasya.custommobtweaks.combat;

import com.destroystokyo.paper.entity.ai.GoalType;
import org.bukkit.event.entity.ProjectileHitEvent;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import org.bukkit.Bukkit;
import org.bukkit.NamespacedKey;
import org.bukkit.entity.AbstractArrow;
import org.bukkit.entity.EnderDragon;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Illusioner;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Mob;
import org.bukkit.entity.Projectile;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.event.entity.EntityShootBowEvent;
import org.bukkit.event.entity.EntityTargetLivingEntityEvent;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.util.Vector;

import java.util.Comparator;
import java.util.UUID;

/** Replaces target selection only; real bow attacks and recursive clone shots stay vanilla. */
public final class IllusionerAggroModule extends ManagedMobModule {
    private final NamespacedKey rootKey;
    private final NamespacedKey projectileFamilyKey;

    public IllusionerAggroModule(CustomMobTweaksPlugin plugin) {
        super(plugin, "illusioner-aggro", 5L);
        rootKey = new NamespacedKey(plugin, "illusioner_clone_root");
        projectileFamilyKey = new NamespacedKey(plugin, "illusioner_arrow_family");
    }

    @Override protected boolean accepts(Entity entity) { return entity instanceof Illusioner; }
    @Override protected Session createSession(Mob entity) { return new AggroSession((Illusioner) entity); }

    private UUID family(Illusioner entity) {
        String stored = entity.getPersistentDataContainer().get(rootKey, PersistentDataType.STRING);
        if (stored != null) {
            try { return UUID.fromString(stored); } catch (IllegalArgumentException ignored) { }
        }
        return entity.getUniqueId();
    }

    private boolean allied(Illusioner attacker, LivingEntity candidate) {
        if (!(candidate instanceof Illusioner other)) return false;
        return flag("protect-all-illusioners", true) || family(attacker).equals(family(other));
    }

    private boolean friendlyProjectile(Projectile projectile, Entity victim) {
        if (!(victim instanceof Illusioner other)) return false;
        String projectileFamily = projectile.getPersistentDataContainer().get(projectileFamilyKey, PersistentDataType.STRING);
        if (projectileFamily != null) {
            return flag("protect-all-illusioners", true) || projectileFamily.equals(family(other).toString());
        }
        // Also protects against arrows already airborne when the module was enabled.
        return projectile.getShooter() instanceof Illusioner shooter
                && (flag("protect-all-illusioners", true)
                || (Bukkit.isOwnedByCurrentRegion(shooter) && allied(shooter, other)));
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void preventFriendlyTarget(EntityTargetLivingEntityEvent event) {
        if (running && event.getEntity() instanceof Illusioner attacker
                && event.getTarget() instanceof Illusioner other
                && Bukkit.isOwnedByCurrentRegion(other) && allied(attacker, other)) event.setCancelled(true);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void preventFriendlyDamage(EntityDamageByEntityEvent event) {
        if (!running || !(event.getEntity() instanceof Illusioner victim)) return;
        Entity source = event.getDamager();
        if ((source instanceof Illusioner attacker && Bukkit.isOwnedByCurrentRegion(attacker) && allied(attacker, victim))
                || (source instanceof Projectile projectile && friendlyProjectile(projectile, victim))) event.setCancelled(true);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void passThroughAllies(ProjectileHitEvent event) {
        if (running && friendlyProjectile(event.getEntity(), event.getHitEntity())) event.setCancelled(true);
    }

    @EventHandler(priority = EventPriority.HIGH, ignoreCancelled = true)
    public void tagShot(EntityShootBowEvent event) {
        if (!running || !(event.getEntity() instanceof Illusioner shooter)
                || !(event.getProjectile() instanceof AbstractArrow arrow)) return;
        arrow.getPersistentDataContainer().set(projectileFamilyKey, PersistentDataType.STRING, family(shooter).toString());
        LivingEntity target = shooter.getTarget();
        if (target instanceof EnderDragon dragon && Bukkit.isOwnedByCurrentRegion(dragon)) {
            // Aim at a real hittable part, not the dragon's empty multipart origin.
            Vector origin = arrow.getLocation().toVector();
            Vector point = dragon.getParts().stream()
                    .filter(Bukkit::isOwnedByCurrentRegion)
                    .map(part -> part.getBoundingBox().getCenter())
                    .min(Comparator.comparingDouble(position -> position.distanceSquared(origin)))
                    .orElse(dragon.getBoundingBox().getCenter());
            Vector delta = point.subtract(origin);
            double speed = arrow.getVelocity().length();
            if (speed > 0.001 && delta.lengthSquared() > 0.001) {
                double horizontal = Math.sqrt(delta.getX() * delta.getX() + delta.getZ() * delta.getZ());
                delta.setY(delta.getY() + horizontal * 0.12);
                arrow.setVelocity(delta.normalize().multiply(speed));
            }
        }
    }

    private final class AggroSession extends Session {
        private final Illusioner illusioner;
        private long nextSearch;
        private long unseenTicks;
        private UUID lastLoggedTarget;

        private AggroSession(Illusioner illusioner) { super(illusioner); this.illusioner = illusioner; }

        @Override protected void initialize() {
            guard("universal_target", () -> true, GoalType.TARGET);
        }

        private boolean enemy(LivingEntity target, double range) {
            return target != illusioner && CombatSupport.usableTarget(illusioner, target, range)
                    && !allied(illusioner, target);
        }

        @Override protected void tick() {
            double range = number("target-range", 32.0, 4.0, 64.0);
            LivingEntity current = illusioner.getTarget();
            if (enemy(current, range)) {
                if (illusioner.hasLineOfSight(current)) unseenTicks = 0L;
                else unseenTicks += 5L;
                if (unseenTicks <= ticks("unseen-memory-ticks", 100, 0, 1200)) {
                    logTarget(current);
                    return;
                }
            }
            if (current != null) illusioner.setTarget(null);
            if (age < nextSearch) return;
            nextSearch = age + ticks("search-period-ticks", 20, 5, 200);
            unseenTicks = 0L;
            LivingEntity target = CombatSupport.nearbyOwned(illusioner.getLocation(), range).stream()
                    .filter(candidate -> enemy(candidate, range))
                    .filter(illusioner::hasLineOfSight)
                    .min(Comparator.comparingDouble(candidate -> candidate.getLocation().distanceSquared(illusioner.getLocation())))
                    .orElse(null);
            if (target != null) illusioner.setTarget(target);
            logTarget(target);
        }

        private void logTarget(LivingEntity target) {
            UUID id = target == null ? null : target.getUniqueId();
            if (java.util.Objects.equals(id, lastLoggedTarget)) return;
            lastLoggedTarget = id;
            debug("target illusioner=" + illusioner.getUniqueId() + " target=" + id
                    + " type=" + (target == null ? "none" : target.getType()));
        }

        @Override protected void cleanup() {
            if (illusioner.isValid()) illusioner.setTarget(null);
        }
    }
}
