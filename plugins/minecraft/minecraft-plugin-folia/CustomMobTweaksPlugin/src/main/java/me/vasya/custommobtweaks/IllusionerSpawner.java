package me.vasya.custommobtweaks;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.World;
import org.bukkit.attribute.Attribute;
import org.bukkit.attribute.AttributeInstance;
import org.bukkit.block.Block;
import org.bukkit.entity.Entity;
import org.bukkit.entity.EntityType;
import org.bukkit.entity.Illusioner;
import org.bukkit.entity.Monster;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.CreatureSpawnEvent;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.event.player.PlayerQuitEvent;
import org.bukkit.inventory.ItemStack;

import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;

public final class IllusionerSpawner implements Listener, PluginComponent {
    private final CustomMobTweaksPlugin plugin;
    private final Set<UUID> scheduledPlayers = ConcurrentHashMap.newKeySet();

    public IllusionerSpawner(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
    }

    @Override
    public void start() {
        Bukkit.getPluginManager().registerEvents(this, plugin);
        for (Player player : Bukkit.getOnlinePlayers()) {
            schedulePeriodicCheck(player);
        }
    }

    @Override
    public void shutdown() {
        HandlerList.unregisterAll(this);
        scheduledPlayers.clear();
    }

    @EventHandler(priority = EventPriority.HIGH, ignoreCancelled = true)
    public void onNaturalMobSpawn(CreatureSpawnEvent event) {
        if (!plugin.enabled("illusioner-spawn")
                || !plugin.getConfig().getBoolean("illusioner-spawn.replace-natural-hostile-mobs", true)
                || !(event.getEntity() instanceof Monster)
                || event.getEntityType() == EntityType.ILLUSIONER
                || !isNaturalLike(event.getSpawnReason())) {
            return;
        }

        double chance = normalizeChance(plugin.getConfig().getDouble("illusioner-spawn.replacement-chance", 0.001D));
        if (ThreadLocalRandom.current().nextDouble() >= chance) {
            return;
        }

        Location location = event.getLocation().clone();
        event.setCancelled(true);
        spawnIllusioner(location);
    }

    @EventHandler
    public void onJoin(PlayerJoinEvent event) {
        schedulePeriodicCheck(event.getPlayer());
    }

    @EventHandler
    public void onQuit(PlayerQuitEvent event) {
        scheduledPlayers.remove(event.getPlayer().getUniqueId());
    }

    private void schedulePeriodicCheck(Player player) {
        if (!scheduledPlayers.add(player.getUniqueId())) {
            return;
        }
        long interval = Math.max(20L, plugin.getConfig().getLong("illusioner-spawn.periodic-check-interval-ticks", 600L));
        player.getScheduler().runAtFixedRate(plugin, task -> {
            if (!player.isOnline()) {
                scheduledPlayers.remove(player.getUniqueId());
                task.cancel();
                return;
            }
            if (!plugin.enabled("illusioner-spawn")
                    || !plugin.getConfig().getBoolean("illusioner-spawn.periodic-spawn.enabled", true)) {
                return;
            }
            double chance = normalizeChance(plugin.getConfig().getDouble("illusioner-spawn.periodic-spawn.chance-per-check", 0.001D));
            if (ThreadLocalRandom.current().nextDouble() >= chance) {
                return;
            }
            queuePeriodicSpawn(player);
        }, () -> scheduledPlayers.remove(player.getUniqueId()), interval, interval);
    }

    private void queuePeriodicSpawn(Player player) {
        World world = player.getWorld();
        Location playerLocation = player.getLocation();
        int minRadius = Math.max(8, plugin.getConfig().getInt("illusioner-spawn.periodic-spawn.min-radius", 24));
        int maxRadius = Math.max(minRadius, plugin.getConfig().getInt("illusioner-spawn.periodic-spawn.max-radius", 48));
        double angle = ThreadLocalRandom.current().nextDouble(0.0D, Math.PI * 2.0D);
        double distance = ThreadLocalRandom.current().nextDouble(minRadius, maxRadius + 0.001D);
        int x = (int) Math.floor(playerLocation.getX() + Math.cos(angle) * distance);
        int z = (int) Math.floor(playerLocation.getZ() + Math.sin(angle) * distance);
        Location regionAnchor = new Location(world, x, world.getMinHeight(), z);

        Bukkit.getRegionScheduler().run(plugin, regionAnchor, task -> {
            int y = world.getHighestBlockYAt(x, z);
            Location location = new Location(world, x + 0.5D, y + 1.0D, z + 0.5D);
            if (!validSpawnLocation(location)) {
                return;
            }
            double nearbyRadius = Math.max(8.0D, plugin.getConfig().getDouble("illusioner-spawn.periodic-spawn.no-other-illusioner-radius", 48.0D));
            for (Entity nearby : world.getNearbyEntities(location, nearbyRadius, nearbyRadius, nearbyRadius)) {
                if (nearby.getType() == EntityType.ILLUSIONER) {
                    return;
                }
            }
            spawnIllusioner(location);
        });
    }

    private void spawnIllusioner(Location location) {
        World world = location.getWorld();
        if (world == null) {
            return;
        }
        Entity spawned = world.spawnEntity(location, EntityType.ILLUSIONER);
        if (!(spawned instanceof Illusioner illusioner)) {
            return;
        }

        // Deliberately no purple name and no forced visible custom name.
        illusioner.setCustomName(null);
        illusioner.setCustomNameVisible(false);

        double healthMultiplier = Math.max(0.1D, plugin.getConfig().getDouble("illusioner-spawn.health-multiplier", 1.0D));
        AttributeInstance maxHealth = illusioner.getAttribute(Attribute.MAX_HEALTH);
        if (maxHealth != null && healthMultiplier != 1.0D) {
            maxHealth.setBaseValue(maxHealth.getBaseValue() * healthMultiplier);
            illusioner.setHealth(maxHealth.getValue());
        }
        if (plugin.getConfig().getBoolean("illusioner-spawn.give-bow", true) && illusioner.getEquipment() != null) {
            illusioner.getEquipment().setItemInMainHand(new ItemStack(Material.BOW));
            illusioner.getEquipment().setItemInMainHandDropChance(0.0F);
        }

        world.spawnParticle(Particle.PORTAL, location.clone().add(0.0D, 1.0D, 0.0D), 30, 0.5D, 0.8D, 0.5D, 0.1D);
        world.playSound(location, Sound.ENTITY_ILLUSIONER_MIRROR_MOVE, 1.0F, 1.0F);
    }

    private boolean validSpawnLocation(Location location) {
        World world = location.getWorld();
        if (world == null) {
            return false;
        }
        Block feet = world.getBlockAt(location);
        Block head = world.getBlockAt(location.clone().add(0.0D, 1.0D, 0.0D));
        Block ground = world.getBlockAt(location.clone().add(0.0D, -1.0D, 0.0D));
        if (!feet.isPassable() || !head.isPassable() || !ground.getType().isSolid()) {
            return false;
        }
        int maxLight = plugin.getConfig().getInt("illusioner-spawn.periodic-spawn.max-block-light", 7);
        return maxLight < 0 || feet.getLightLevel() <= maxLight;
    }

    private boolean isNaturalLike(CreatureSpawnEvent.SpawnReason reason) {
        return reason == CreatureSpawnEvent.SpawnReason.NATURAL
                || reason == CreatureSpawnEvent.SpawnReason.PATROL
                || reason == CreatureSpawnEvent.SpawnReason.VILLAGE_INVASION
                || reason == CreatureSpawnEvent.SpawnReason.REINFORCEMENTS;
    }

    private double normalizeChance(double value) {
        if (value > 1.0D) {
            value /= 100.0D;
        }
        return Math.max(0.0D, Math.min(1.0D, value));
    }
}
