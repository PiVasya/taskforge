package me.vasya.custommobtweaks.dragon;

import com.destroystokyo.paper.event.entity.EnderDragonFireballHitEvent;
import com.destroystokyo.paper.event.entity.EnderDragonFlameEvent;
import com.destroystokyo.paper.event.entity.EnderDragonShootFireballEvent;
import com.destroystokyo.paper.event.entity.EntityAddToWorldEvent;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import me.vasya.custommobtweaks.PluginComponent;
import org.bukkit.Bukkit;
import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.World;
import org.bukkit.NamespacedKey;
import org.bukkit.entity.AreaEffectCloud;
import org.bukkit.entity.DragonFireball;
import org.bukkit.entity.EnderCrystal;
import org.bukkit.entity.EnderDragon;
import org.bukkit.entity.Phantom;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.EnderDragonChangePhaseEvent;
import org.bukkit.event.entity.EntityCombustEvent;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.event.entity.EntityDamageEvent;
import org.bukkit.event.entity.EntityDeathEvent;
import org.bukkit.event.entity.EntityRemoveEvent;
import org.bukkit.event.entity.EntityToggleGlideEvent;
import org.bukkit.event.entity.EntityRegainHealthEvent;
import org.bukkit.event.entity.ProjectileLaunchEvent;
import org.bukkit.event.player.PlayerChangedWorldEvent;
import org.bukkit.event.world.EntitiesLoadEvent;
import org.bukkit.event.world.WorldLoadEvent;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.util.Vector;

/** Folia-safe Ender Dragon rework with a real fire stream and purple phantom flocks. */
public final class EnderDragonRework implements PluginComponent, Listener {
    private final CustomMobTweaksPlugin plugin;
    private final DragonBreathAttack breathAttack;
    private final DragonPortalAbilities portalAbilities;
    private final DragonPhantomManager phantomManager;
    private final NamespacedKey replacedFireballKey;

    public EnderDragonRework(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
        this.breathAttack = new DragonBreathAttack(plugin);
        this.portalAbilities = new DragonPortalAbilities(plugin);
        this.phantomManager = new DragonPhantomManager(plugin, portalAbilities);
        this.replacedFireballKey = new NamespacedKey(plugin, "dragon_stream_replaced");
    }

    @Override
    public void start() {
        Bukkit.getPluginManager().registerEvents(this, plugin);
        boolean enabled = plugin.enabled("ender-dragon-rework");
        if (enabled) {
            breathAttack.enable();
            portalAbilities.start();
            phantomManager.start();
        }
        plugin.getLogger().info("[dragon] module started enabled=" + enabled
                + " fireStream=" + plugin.getConfig().getBoolean(
                "ender-dragon-rework.fire-stream.enabled", true)
                + " replaceStrafingFireball=" + plugin.getConfig().getBoolean(
                "ender-dragon-rework.fire-stream.replace-strafing-fireball", true)
                + " replacePerchedBreath=" + plugin.getConfig().getBoolean(
                "ender-dragon-rework.fire-stream.replace-perched-breath", true)
                + " durationTicks=" + plugin.getConfig().getLong(
                "ender-dragon-rework.fire-stream.duration-ticks", 140L)
                + " realGroundFire=" + plugin.getConfig().getBoolean(
                "ender-dragon-rework.fire-stream.ground.place-fire", true)
                + " breathClouds=" + plugin.getConfig().getBoolean(
                "ender-dragon-rework.fire-stream.ground.create-dragon-breath-clouds", true)
                + " phantomFlock=" + plugin.getConfig().getInt(
                "ender-dragon-rework.phantoms.flock-size-min", 5)
                + "-" + plugin.getConfig().getInt(
                "ender-dragon-rework.phantoms.flock-size-max", 6)
                + " phantomMaximumActive=" + plugin.getConfig().getInt(
                "ender-dragon-rework.phantoms.maximum-active", 12)
                + " landingHorizontalRadius=" + plugin.getConfig().getDouble(
                "ender-dragon-rework.phantoms.landing-horizontal-radius", 8.0D)
                + " landingConfirmationSamples=" + plugin.getConfig().getInt(
                "ender-dragon-rework.phantoms.landing-confirmation-samples", 2)
                + " healthMultiplier=" + plugin.getConfig().getDouble(
                "ender-dragon-rework.primary-dragon.health-multiplier", 2.0D)
                + " voidRoar=" + plugin.getConfig().getBoolean(
                "ender-dragon-rework.void-roar.enabled", true)
                + " crystalField=" + plugin.getConfig().getBoolean(
                "ender-dragon-rework.crystal-field.enabled", true)
                + " crystalCount=" + plugin.getConfig().getInt(
                "ender-dragon-rework.crystal-field.count-min", 18)
                + "-" + plugin.getConfig().getInt(
                "ender-dragon-rework.crystal-field.count-max", 24));
    }

    @Override
    public void shutdown() {
        debug("module shutdown start");
        HandlerList.unregisterAll(this);
        phantomManager.shutdown();
        portalAbilities.shutdown();
        breathAttack.shutdown();
        debug("module shutdown complete");
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDragonShootFireball(EnderDragonShootFireballEvent event) {
        EnderDragon dragon = event.getEntity();
        phantomManager.handlePrimaryActivity(dragon, "shoot-fireball-event");
        boolean legacyDragonling = phantomManager.isLegacyDragonling(dragon);
        debug("shoot event dragon=" + dragon.getUniqueId()
                + " legacyDragonling=" + legacyDragonling
                + " battlePresent=" + (dragon.getDragonBattle() != null)
                + " phase=" + dragon.getPhase()
                + " initiallyCancelled=" + event.isCancelled());

        if (!plugin.enabled("ender-dragon-rework")) {
            debug("shoot event ignored reason=module-disabled dragon=" + dragon.getUniqueId());
            return;
        }
        if (legacyDragonling) {
            event.setCancelled(true);
            debug("legacy dragonling fireball cancelled dragon=" + dragon.getUniqueId());
            return;
        }
        if (!plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.enabled", true)
                || !plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.replace-strafing-fireball", true)) {
            debug("shoot event kept vanilla reason=fire-stream-or-strafing-replacement-disabled dragon="
                    + dragon.getUniqueId());
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
        breathAttack.start(dragon, fireball.getLocation().clone(), direction, "vanilla-strafing-fireball");
    }

    /** Fallback if another server path launches a DragonFireball without the dedicated Paper event. */
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onProjectileLaunch(ProjectileLaunchEvent event) {
        if (!(event.getEntity() instanceof DragonFireball fireball)
                || !(fireball.getShooter() instanceof EnderDragon dragon)) {
            return;
        }

        phantomManager.handlePrimaryActivity(dragon, "projectile-launch-fallback");
        boolean legacyDragonling = phantomManager.isLegacyDragonling(dragon);
        boolean alreadyReplaced = fireball.getPersistentDataContainer()
                .has(replacedFireballKey, PersistentDataType.BYTE);
        debug("projectile launch dragonFireball=" + fireball.getUniqueId()
                + " dragon=" + dragon.getUniqueId()
                + " legacyDragonling=" + legacyDragonling
                + " alreadyReplaced=" + alreadyReplaced
                + " initiallyCancelled=" + event.isCancelled());

        if (legacyDragonling) {
            event.setCancelled(true);
            debug("legacy dragonling projectile fallback cancelled dragon=" + dragon.getUniqueId());
            return;
        }
        if (!plugin.enabled("ender-dragon-rework")
                || !plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.enabled", true)
                || !plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.replace-strafing-fireball", true)) {
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
        breathAttack.start(dragon, fireball.getLocation().clone(), direction, "projectile-launch-fallback");
    }

    /** Cleans clouds produced by old dragonling entities left by 2.3.x. */
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDragonFireballHit(EnderDragonFireballHitEvent event) {
        if (!(event.getEntity().getShooter() instanceof EnderDragon dragon)
                || !phantomManager.isLegacyDragonling(dragon)) {
            return;
        }
        event.setCancelled(true);
        if (event.getAreaEffectCloud().isValid()) {
            event.getAreaEffectCloud().remove();
        }
        debug("legacy dragonling fireball cloud cancelled dragon=" + dragon.getUniqueId());
    }

    /** Replaces the vanilla perched breath cloud with the same seven-second mega fire stream. */
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDragonFlame(EnderDragonFlameEvent event) {
        EnderDragon dragon = event.getEntity();
        AreaEffectCloud cloud = event.getAreaEffectCloud();
        Location cloudLocation = cloud.getLocation().clone();
        boolean legacyDragonling = phantomManager.isLegacyDragonling(dragon);
        debug("perched flame event dragon=" + dragon.getUniqueId()
                + " legacyDragonling=" + legacyDragonling
                + " phase=" + dragon.getPhase()
                + " cloud=" + cloud.getUniqueId()
                + " cloudLocation=" + formatLocation(cloudLocation)
                + " initiallyCancelled=" + event.isCancelled());

        if (legacyDragonling) {
            event.setCancelled(true);
            if (cloud.isValid()) {
                cloud.remove();
            }
            debug("legacy dragonling flame cloud cancelled dragon=" + dragon.getUniqueId());
            return;
        }
        if (!plugin.enabled("ender-dragon-rework")) {
            return;
        }

        // The perched flame callback is also the strongest possible landing confirmation.
        phantomManager.handlePerchedBreath(dragon, cloudLocation, "perched-flame-event");

        if (!plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.enabled", true)
                || !plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.replace-perched-breath", true)) {
            debug("perched flame kept vanilla reason=fire-stream-or-perched-replacement-disabled dragon="
                    + dragon.getUniqueId());
            return;
        }

        Location origin = dragon.getEyeLocation().clone();
        Vector direction = perchedBreathDirection(dragon, origin, cloudLocation);
        event.setCancelled(true);
        if (cloud.isValid()) {
            cloud.remove();
        }
        debug("vanilla perched breath replaced dragon=" + dragon.getUniqueId()
                + " origin=" + formatLocation(origin)
                + " target=" + formatLocation(cloudLocation)
                + " direction=" + formatVector(direction));
        breathAttack.start(dragon, origin, direction, "vanilla-perched-breath");
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDragonPhaseChange(EnderDragonChangePhaseEvent event) {
        if (!plugin.enabled("ender-dragon-rework")) {
            return;
        }
        phantomManager.handlePhaseChange(event);
        if (event.getNewPhase() == EnderDragon.Phase.BREATH_ATTACK
                && plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.enabled", true)
                && plugin.getConfig().getBoolean("ender-dragon-rework.fire-stream.replace-perched-breath", true)) {
            scheduleBreathPhaseFallback(event.getEntity());
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onLegacyDragonlingRegainHealth(EntityRegainHealthEvent event) {
        if (event.getEntity() instanceof EnderDragon dragon && phantomManager.isLegacyDragonling(dragon)) {
            event.setCancelled(true);
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onEndCrystalDamage(EntityDamageEvent event) {
        if (!(event.getEntity() instanceof EnderCrystal crystal)) {
            return;
        }
        if (!isDragonBreathDamage(crystal, event)) {
            return;
        }
        event.setCancelled(true);
        debug("ender crystal protected from dragon breath crystal=" + crystal.getUniqueId()
                + " managed=" + portalAbilities.isDragonCrystal(crystal)
                + " cause=" + event.getCause());
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onElytraToggle(EntityToggleGlideEvent event) {
        if (!(event.getEntity() instanceof Player player)
                || !event.isGliding()
                || !portalAbilities.isElytraLocked(player)) {
            return;
        }
        event.setCancelled(true);
        debug("elytra activation blocked by void roar player="
                + player.getName() + "/" + player.getUniqueId());
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDragonPhantomAttack(EntityDamageByEntityEvent event) {
        if (!(event.getDamager() instanceof Phantom phantom) || !phantomManager.isDragonPhantom(phantom)) {
            return;
        }
        if (!(event.getEntity() instanceof Player player)
                || player.isDead()
                || player.getGameMode() == GameMode.SPECTATOR) {
            event.setCancelled(true);
            return;
        }
        if (event.isCancelled()) {
            return;
        }

        double damage = Math.max(0.0D, plugin.getConfig().getDouble(
                "ender-dragon-rework.phantoms.attack-damage", 6.0D));
        event.setDamage(damage);
        debug("dragon phantom attack phantom=" + phantom.getUniqueId()
                + " player=" + player.getName() + "/" + player.getUniqueId()
                + " damage=" + damage);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDragonPhantomCombust(EntityCombustEvent event) {
        if (event.getEntity() instanceof Phantom phantom && phantomManager.isDragonPhantom(phantom)) {
            event.setCancelled(true);
            phantom.setFireTicks(0);
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST)
    public void onEntityDeath(EntityDeathEvent event) {
        if (event.getEntity() instanceof Phantom phantom && phantomManager.isDragonPhantom(phantom)) {
            event.getDrops().clear();
            event.setDroppedExp(0);
            phantomManager.handlePhantomDeath(phantom);
            return;
        }
        if (!(event.getEntity() instanceof EnderDragon dragon)) {
            return;
        }
        if (phantomManager.isLegacyDragonling(dragon)) {
            event.getDrops().clear();
            event.setDroppedExp(0);
            return;
        }
        debug("primary dragon death uuid=" + dragon.getUniqueId()
                + " battlePresent=" + (dragon.getDragonBattle() != null));
        phantomManager.handlePrimaryDragonDeath(dragon);
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onWorldLoad(WorldLoadEvent event) {
        phantomManager.inspectWorldBattle(event.getWorld(), "world-load");
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onPlayerChangedWorld(PlayerChangedWorldEvent event) {
        World world = event.getPlayer().getWorld();
        if (world.getEnvironment() == World.Environment.THE_END) {
            phantomManager.inspectWorldBattle(world, "player-enter-end:" + event.getPlayer().getUniqueId());
        }
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onEntityAddToWorld(EntityAddToWorldEvent event) {
        phantomManager.handleEntityAdded(event.getEntity(), "entity-add-to-world");
        portalAbilities.handleEntityAdded(event.getEntity(), "entity-add-to-world");
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onEntitiesLoad(EntitiesLoadEvent event) {
        phantomManager.handleEntitiesLoad(event.getEntities());
        portalAbilities.handleEntitiesLoad(event.getEntities());
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onEntityRemove(EntityRemoveEvent event) {
        portalAbilities.handleEntityRemoved(event.getEntity());
    }

    public String diagnosticsSummary() {
        return "observedPrimaries=" + phantomManager.observedPrimaryCount()
                + " activeStreams=" + breathAttack.activeAttackCount()
                + " activeDragonPhantoms=" + phantomManager.activePhantomCount()
                + " activeVoidRoars=" + portalAbilities.activeRoarCount()
                + " activeDragonCrystals=" + portalAbilities.activeCrystalCount();
    }

    private void scheduleBreathPhaseFallback(EnderDragon dragon) {
        dragon.getScheduler().runDelayed(plugin, task -> {
            if (!plugin.enabled("ender-dragon-rework")
                    || dragon.isDead()
                    || !dragon.isValid()
                    || dragon.getPhase() != EnderDragon.Phase.BREATH_ATTACK
                    || breathAttack.hasActiveAttack(dragon)) {
                return;
            }
            Location origin = dragon.getEyeLocation().clone();
            Vector direction = dragon.getLocation().getDirection();
            if (direction.lengthSquared() < 0.0001D) {
                direction = new Vector(0.0D, -0.20D, 1.0D);
            }
            debug("breath phase fallback starting stream dragon=" + dragon.getUniqueId()
                    + " origin=" + formatLocation(origin)
                    + " direction=" + formatVector(direction));
            breathAttack.start(dragon, origin, direction.normalize(), "breath-phase-fallback");
        }, () -> debug("breath phase fallback retired dragon=" + dragon.getUniqueId()), 2L);
    }

    private Vector perchedBreathDirection(EnderDragon dragon, Location origin, Location cloudLocation) {
        Vector direction = cloudLocation.toVector().subtract(origin.toVector());
        if (direction.lengthSquared() < 1.0D) {
            direction = dragon.getLocation().getDirection();
        }
        if (direction.lengthSquared() < 0.0001D) {
            direction = new Vector(0.0D, -0.20D, 1.0D);
        }
        return direction.normalize();
    }

    private boolean isDragonBreathDamage(EnderCrystal crystal, EntityDamageEvent event) {
        if (event instanceof EntityDamageByEntityEvent byEntity) {
            org.bukkit.entity.Entity damager = byEntity.getDamager();
            if (damager instanceof EnderDragon) {
                return true;
            }
            if (damager instanceof DragonFireball fireball
                    && fireball.getShooter() instanceof EnderDragon) {
                return true;
            }
            if (damager instanceof AreaEffectCloud cloud) {
                if (breathAttack.isDragonBreathCloud(cloud)) {
                    return true;
                }
                return cloud.getSource() instanceof EnderDragon;
            }
        }
        // Damage from an AreaEffectCloud can be surfaced as plain MAGIC on some server paths,
        // while real fire placed by the custom stream has no owning entity. Keep that fallback
        // limited to the crystals spawned by this module so player-created crystals remain vanilla.
        return portalAbilities.isDragonCrystal(crystal)
                && (event.getCause() == EntityDamageEvent.DamageCause.MAGIC
                || event.getCause() == EntityDamageEvent.DamageCause.FIRE
                || event.getCause() == EntityDamageEvent.DamageCause.FIRE_TICK);
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
