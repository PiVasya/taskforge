package me.vasya.custommobtweaks.dragon;

import com.destroystokyo.paper.event.entity.EnderDragonFireballHitEvent;
import com.destroystokyo.paper.event.entity.EnderDragonFlameEvent;
import com.destroystokyo.paper.event.entity.EnderDragonShootFireballEvent;
import com.destroystokyo.paper.event.entity.EntityAddToWorldEvent;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import me.vasya.custommobtweaks.PluginComponent;
import org.bukkit.Bukkit;
import org.bukkit.GameMode;
import org.bukkit.NamespacedKey;
import org.bukkit.entity.DragonFireball;
import org.bukkit.entity.EnderDragon;
import org.bukkit.entity.EnderDragonPart;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.EnderDragonChangePhaseEvent;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.event.entity.EntityDeathEvent;
import org.bukkit.event.entity.EntityRegainHealthEvent;
import org.bukkit.event.entity.ProjectileLaunchEvent;
import org.bukkit.event.world.EntitiesLoadEvent;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.util.Vector;

/** Folia-safe Ender Dragon rework entry point with explicit event diagnostics. */
public final class EnderDragonRework implements PluginComponent, Listener {
    private final CustomMobTweaksPlugin plugin;
    private final DragonBreathAttack breathAttack;
    private final DragonlingManager dragonlingManager;
    private final NamespacedKey replacedFireballKey;

    public EnderDragonRework(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
        this.breathAttack = new DragonBreathAttack(plugin);
        this.dragonlingManager = new DragonlingManager(plugin);
        this.replacedFireballKey = new NamespacedKey(plugin, "dragon_stream_replaced");
    }

    @Override
    public void start() {
        Bukkit.getPluginManager().registerEvents(this, plugin);
        boolean enabled = plugin.enabled("ender-dragon-rework");
        if (enabled) {
            breathAttack.enable();
            dragonlingManager.start();
        }
        plugin.getLogger().info("[dragon] module started enabled=" + enabled
                + " fireStream=" + plugin.getConfig().getBoolean(
                        "ender-dragon-rework.fire-stream.enabled", true)
                + " durationTicks=" + plugin.getConfig().getLong(
                        "ender-dragon-rework.fire-stream.duration-ticks", 140L)
                + " realGroundFire=" + plugin.getConfig().getBoolean(
                        "ender-dragon-rework.fire-stream.ground.place-fire", true)
                + " breathClouds=" + plugin.getConfig().getBoolean(
                        "ender-dragon-rework.fire-stream.ground.create-dragon-breath-clouds", true)
                + " dragonlingMax=" + plugin.getConfig().getInt(
                        "ender-dragon-rework.dragonling.maximum-active", 2));
    }

    @Override
    public void shutdown() {
        debug("module shutdown start");
        HandlerList.unregisterAll(this);
        breathAttack.shutdown();
        dragonlingManager.shutdown();
        debug("module shutdown complete");
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDragonShootFireball(EnderDragonShootFireballEvent event) {
        EnderDragon dragon = event.getEntity();
        boolean dragonling = dragonlingManager.isDragonling(dragon);
        debug("shoot event dragon=" + dragon.getUniqueId()
                + " dragonling=" + dragonling
                + " battlePresent=" + (dragon.getDragonBattle() != null)
                + " phase=" + dragon.getPhase()
                + " initiallyCancelled=" + event.isCancelled());

        if (!plugin.enabled("ender-dragon-rework")) {
            debug("shoot event ignored reason=module-disabled dragon=" + dragon.getUniqueId());
            return;
        }
        if (dragonling) {
            event.setCancelled(true);
            debug("dragonling fireball cancelled dragon=" + dragon.getUniqueId());
            return;
        }
        if (!plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.enabled", true)) {
            debug("shoot event kept vanilla reason=fire-stream-disabled dragon=" + dragon.getUniqueId());
            return;
        }

        DragonFireball fireball = event.getFireball();
        Vector direction = projectileDirection(dragon, fireball);
        if (direction.lengthSquared() < 0.0001D) {
            debug("shoot event could not resolve direction dragon=" + dragon.getUniqueId());
            return;
        }

        fireball.getPersistentDataContainer().set(replacedFireballKey, PersistentDataType.BYTE, (byte) 1);
        event.setCancelled(true);
        debug("vanilla fireball replaced dragon=" + dragon.getUniqueId()
                + " fireball=" + fireball.getUniqueId()
                + " origin=" + formatLocation(fireball.getLocation())
                + " direction=" + formatVector(direction));
        breathAttack.start(dragon, fireball.getLocation().clone(), direction);
    }

    /** Fallback if another server path launches a DragonFireball without the dedicated Paper event. */
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onProjectileLaunch(ProjectileLaunchEvent event) {
        if (!(event.getEntity() instanceof DragonFireball fireball)
                || !(fireball.getShooter() instanceof EnderDragon dragon)) {
            return;
        }

        boolean dragonling = dragonlingManager.isDragonling(dragon);
        boolean alreadyReplaced = fireball.getPersistentDataContainer()
                .has(replacedFireballKey, PersistentDataType.BYTE);
        debug("projectile launch dragonFireball=" + fireball.getUniqueId()
                + " dragon=" + dragon.getUniqueId()
                + " dragonling=" + dragonling
                + " alreadyReplaced=" + alreadyReplaced
                + " initiallyCancelled=" + event.isCancelled());

        if (dragonling) {
            event.setCancelled(true);
            debug("dragonling projectile fallback cancelled dragon=" + dragon.getUniqueId());
            return;
        }
        if (!plugin.enabled("ender-dragon-rework")
                || !plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.enabled", true)) {
            return;
        }

        event.setCancelled(true);
        if (alreadyReplaced) {
            return;
        }
        fireball.getPersistentDataContainer().set(replacedFireballKey, PersistentDataType.BYTE, (byte) 1);
        Vector direction = projectileDirection(dragon, fireball);
        if (direction.lengthSquared() < 0.0001D) {
            debug("projectile fallback could not resolve direction dragon=" + dragon.getUniqueId());
            return;
        }
        debug("projectile fallback replaced dragon=" + dragon.getUniqueId()
                + " fireball=" + fireball.getUniqueId()
                + " origin=" + formatLocation(fireball.getLocation())
                + " direction=" + formatVector(direction));
        breathAttack.start(dragon, fireball.getLocation().clone(), direction);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDragonFireballHit(EnderDragonFireballHitEvent event) {
        if (!(event.getEntity().getShooter() instanceof EnderDragon dragon)
                || !dragonlingManager.isDragonling(dragon)) {
            return;
        }
        event.setCancelled(true);
        if (event.getAreaEffectCloud().isValid()) {
            event.getAreaEffectCloud().remove();
        }
        debug("dragonling fireball hit cancelled dragon=" + dragon.getUniqueId());
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDragonFlame(EnderDragonFlameEvent event) {
        if (!dragonlingManager.isDragonling(event.getEntity())) {
            return;
        }
        event.setCancelled(true);
        if (event.getAreaEffectCloud().isValid()) {
            event.getAreaEffectCloud().remove();
        }
        debug("dragonling flame cloud cancelled dragon=" + event.getEntity().getUniqueId());
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDragonPhaseChange(EnderDragonChangePhaseEvent event) {
        if (!plugin.enabled("ender-dragon-rework")) {
            return;
        }
        dragonlingManager.handlePhaseChange(event);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onDragonRegainHealth(EntityRegainHealthEvent event) {
        if (event.getEntity() instanceof EnderDragon dragon && dragonlingManager.isDragonling(dragon)) {
            event.setCancelled(true);
            debug("dragonling healing cancelled uuid=" + dragon.getUniqueId()
                    + " amount=" + event.getAmount()
                    + " reason=" + event.getRegainReason());
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDragonCollision(EntityDamageByEntityEvent event) {
        EnderDragon dragon = dragonFromDamager(event.getDamager());
        if (dragon == null || !dragonlingManager.isDragonling(dragon)) {
            return;
        }
        if (!(event.getEntity() instanceof Player player)
                || player.isDead()
                || player.getGameMode() == GameMode.SPECTATOR
                || player.getGameMode() == GameMode.CREATIVE) {
            event.setCancelled(true);
            return;
        }

        if (event.isCancelled()) {
            debug("dragonling collision kept cancelled by earlier listener dragon=" + dragon.getUniqueId()
                    + " player=" + player.getName() + "/" + player.getUniqueId());
            return;
        }

        double damage = Math.max(0.0D, plugin.getConfig().getDouble(
                "ender-dragon-rework.dragonling.collision-damage", 6.0D));
        event.setDamage(damage);
        debug("dragonling collision dragon=" + dragon.getUniqueId()
                + " player=" + player.getName() + "/" + player.getUniqueId()
                + " damage=" + damage);
    }

    @EventHandler(priority = EventPriority.HIGHEST)
    public void onDragonDeath(EntityDeathEvent event) {
        if (!(event.getEntity() instanceof EnderDragon dragon)) {
            return;
        }
        if (dragonlingManager.isDragonling(dragon)) {
            event.getDrops().clear();
            event.setDroppedExp(0);
            dragonlingManager.handleDragonlingDeath(dragon);
            return;
        }
        debug("primary dragon death uuid=" + dragon.getUniqueId()
                + " battlePresent=" + (dragon.getDragonBattle() != null));
        dragonlingManager.handlePrimaryDragonDeath(dragon);
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onEntityAddToWorld(EntityAddToWorldEvent event) {
        if (event.getEntity() instanceof EnderDragon dragon) {
            dragonlingManager.handleDragonAdded(dragon, "entity-add-to-world");
        }
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onEntitiesLoad(EntitiesLoadEvent event) {
        dragonlingManager.handleEntitiesLoad(event.getEntities());
    }

    private Vector projectileDirection(EnderDragon dragon, DragonFireball fireball) {
        Vector direction = fireball.getAcceleration().clone();
        if (direction.lengthSquared() < 0.0001D) {
            direction = fireball.getVelocity().clone();
        }
        if (direction.lengthSquared() < 0.0001D) {
            direction = dragon.getLocation().getDirection();
        }
        return direction.lengthSquared() < 0.0001D ? new Vector() : direction.normalize();
    }

    private EnderDragon dragonFromDamager(Entity damager) {
        if (damager instanceof EnderDragon dragon) {
            return dragon;
        }
        if (damager instanceof EnderDragonPart part) {
            return part.getParent();
        }
        return null;
    }

    private boolean debugEnabled() {
        return plugin.getConfig().getBoolean("ender-dragon-rework.debug",
                plugin.getConfig().getBoolean("messages.debug", false));
    }

    private void debug(String message) {
        if (debugEnabled()) {
            plugin.getLogger().info("[dragon] " + message);
        }
    }

    private String formatLocation(org.bukkit.Location location) {
        return String.format(java.util.Locale.ROOT, "%.2f,%.2f,%.2f",
                location.getX(), location.getY(), location.getZ());
    }

    private String formatVector(Vector vector) {
        return String.format(java.util.Locale.ROOT, "%.3f,%.3f,%.3f",
                vector.getX(), vector.getY(), vector.getZ());
    }
}
