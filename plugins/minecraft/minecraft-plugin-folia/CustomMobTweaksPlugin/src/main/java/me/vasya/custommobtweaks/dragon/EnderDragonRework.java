package me.vasya.custommobtweaks.dragon;

import com.destroystokyo.paper.event.entity.EnderDragonFireballHitEvent;
import com.destroystokyo.paper.event.entity.EnderDragonFlameEvent;
import com.destroystokyo.paper.event.entity.EnderDragonShootFireballEvent;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import me.vasya.custommobtweaks.PluginComponent;
import org.bukkit.Bukkit;
import org.bukkit.GameMode;
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
import org.bukkit.util.Vector;

/**
 * Folia-safe entry point for the Ender Dragon changes. The vanilla primary dragon keeps its
 * normal fight AI. Only its real fireball shot is replaced, while marked dragonlings are managed
 * independently and are prevented from entering every vanilla breath/perch attack phase.
 */
public final class EnderDragonRework implements PluginComponent, Listener {
    private final CustomMobTweaksPlugin plugin;
    private final DragonBreathAttack breathAttack;
    private final DragonlingManager dragonlingManager;

    public EnderDragonRework(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
        this.breathAttack = new DragonBreathAttack(plugin);
        this.dragonlingManager = new DragonlingManager(plugin);
    }

    @Override
    public void start() {
        Bukkit.getPluginManager().registerEvents(this, plugin);
        breathAttack.enable();
        dragonlingManager.start();
    }

    @Override
    public void shutdown() {
        HandlerList.unregisterAll(this);
        breathAttack.shutdown();
        dragonlingManager.shutdown();
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onDragonShootFireball(EnderDragonShootFireballEvent event) {
        if (!plugin.enabled("ender-dragon-rework")) {
            return;
        }

        EnderDragon dragon = event.getEntity();
        if (dragonlingManager.isDragonling(dragon)) {
            event.setCancelled(true);
            return;
        }
        if (!dragonlingManager.isPrimaryDragon(dragon)
                || !plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.enabled", true)) {
            return;
        }

        DragonFireball fireball = event.getFireball();
        Vector direction = fireball.getAcceleration().clone();
        if (direction.lengthSquared() < 0.0001D) {
            direction = fireball.getVelocity().clone();
        }
        if (direction.lengthSquared() < 0.0001D) {
            direction = dragon.getLocation().getDirection();
        }
        if (direction.lengthSquared() < 0.0001D) {
            return;
        }

        event.setCancelled(true);
        breathAttack.start(dragon, fireball.getLocation().clone(), direction.normalize());
    }

    /** Fallback guard for projectiles created by another plugin after Paper's dragon event. */
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onProjectileLaunch(ProjectileLaunchEvent event) {
        if (!(event.getEntity() instanceof DragonFireball fireball)
                || !(fireball.getShooter() instanceof EnderDragon dragon)
                || !dragonlingManager.isDragonling(dragon)) {
            return;
        }
        event.setCancelled(true);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onDragonFireballHit(EnderDragonFireballHitEvent event) {
        if (!(event.getEntity().getShooter() instanceof EnderDragon dragon)
                || !dragonlingManager.isDragonling(dragon)) {
            return;
        }
        event.setCancelled(true);
        if (event.getAreaEffectCloud().isValid()) {
            event.getAreaEffectCloud().remove();
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onDragonFlame(EnderDragonFlameEvent event) {
        if (!dragonlingManager.isDragonling(event.getEntity())) {
            return;
        }
        event.setCancelled(true);
        if (event.getAreaEffectCloud().isValid()) {
            event.getAreaEffectCloud().remove();
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
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
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
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

        double damage = Math.max(0.0D, plugin.getConfig().getDouble(
                "ender-dragon-rework.dragonling.collision-damage", 6.0D));
        event.setDamage(damage);
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
        if (dragonlingManager.isPrimaryDragon(dragon)) {
            dragonlingManager.handlePrimaryDragonDeath(dragon);
        }
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onEntitiesLoad(EntitiesLoadEvent event) {
        dragonlingManager.handleEntitiesLoad(event.getEntities());
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
}
