package me.vasya.custommobtweaks;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.entity.Entity;
import org.bukkit.entity.EntityType;
import org.bukkit.entity.Player;
import org.bukkit.entity.Projectile;
import org.bukkit.event.EventHandler;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.ProjectileHitEvent;
import org.bukkit.event.player.PlayerQuitEvent;
import org.bukkit.inventory.ItemStack;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.util.Vector;

import java.util.ArrayList;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;

public final class HappyGhastBomberListener implements Listener, PluginComponent {
    private final CustomMobTweaksPlugin plugin;
    private final RadiationManager radiationManager;
    private final NamespacedKey bomberProjectileKey;
    private final Map<UUID, ScheduledTask> activeBombers = new ConcurrentHashMap<>();

    public HappyGhastBomberListener(CustomMobTweaksPlugin plugin, RadiationManager radiationManager) {
        this.plugin = plugin;
        this.radiationManager = radiationManager;
        this.bomberProjectileKey = new NamespacedKey(plugin, "happy_ghast_bomber_projectile");
    }

    @Override
    public void start() {
        Bukkit.getPluginManager().registerEvents(this, plugin);
    }

    @Override
    public void shutdown() {
        HandlerList.unregisterAll(this);
        for (ScheduledTask task : new ArrayList<>(activeBombers.values())) {
            if (task != null && !task.isCancelled()) {
                task.cancel();
            }
        }
        activeBombers.clear();
    }

    public boolean activate(Player player) {
        if (!plugin.enabled("happy-ghast-bomber")) {
            player.sendMessage("§cРежим бомбардировщика отключён в конфиге.");
            return false;
        }
        if (player.getVehicle() == null || player.getVehicle().getType() != EntityType.HAPPY_GHAST) {
            player.sendMessage("§cДля включения режима нужно сидеть на счастливом гасте.");
            return false;
        }
        if (activeBombers.containsKey(player.getUniqueId())) {
            player.sendMessage("§eРежим бомбардировщика уже включён.");
            return true;
        }

        UUID uuid = player.getUniqueId();
        long basePeriod = Math.max(1L, plugin.getConfig().getLong("happy-ghast-bomber.slow-drop-interval-ticks", 20L));
        ScheduledTask task = player.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
            if (!player.isOnline()
                    || player.getVehicle() == null
                    || player.getVehicle().getType() != EntityType.HAPPY_GHAST
                    || !plugin.enabled("happy-ghast-bomber")) {
                activeBombers.remove(uuid);
                scheduledTask.cancel();
                return;
            }

            int tnt = countTnt(player);
            if (tnt <= 0) {
                player.sendMessage("§cTNT закончилось — режим бомбардировщика выключен.");
                activeBombers.remove(uuid);
                scheduledTask.cancel();
                return;
            }

            int threshold = Math.max(1, plugin.getConfig().getInt("happy-ghast-bomber.fast-mode-min-tnt", 10));
            long fastPeriod = Math.max(1L, plugin.getConfig().getLong("happy-ghast-bomber.fast-drop-interval-ticks", 20L));
            long desiredPeriod = tnt >= threshold ? fastPeriod : basePeriod;
            long tick = player.getTicksLived();
            if (tick % desiredPeriod != 0L) {
                return;
            }

            int minCost = Math.max(1, plugin.getConfig().getInt("happy-ghast-bomber.min-tnt-cost", 1));
            int maxCost = Math.max(minCost, plugin.getConfig().getInt("happy-ghast-bomber.max-tnt-cost", 5));
            int cost = Math.min(tnt, ThreadLocalRandom.current().nextInt(minCost, maxCost + 1));
            removeTnt(player, cost);
            dropProjectiles(player);
        }, () -> activeBombers.remove(uuid), 1L, 1L);
        if (task == null) {
            player.sendMessage("§cНе удалось запустить региональную задачу бомбардировщика.");
            return false;
        }
        activeBombers.put(uuid, task);
        player.sendMessage("§aРежим бомбардировщика включён.");
        return true;
    }

    public boolean deactivate(Player player) {
        ScheduledTask task = activeBombers.remove(player.getUniqueId());
        if (task == null) {
            player.sendMessage("§eРежим бомбардировщика не был включён.");
            return false;
        }
        task.cancel();
        player.sendMessage("§aРежим бомбардировщика выключен.");
        return true;
    }

    public boolean isActive(Player player) {
        ScheduledTask task = activeBombers.get(player.getUniqueId());
        return task != null && !task.isCancelled();
    }

    @EventHandler(ignoreCancelled = true)
    public void onProjectileHit(ProjectileHitEvent event) {
        Projectile projectile = event.getEntity();
        if (!projectile.getPersistentDataContainer().has(bomberProjectileKey, PersistentDataType.BYTE)) {
            return;
        }
        Location location = projectile.getLocation().clone();
        location.getWorld().spawnParticle(Particle.EXPLOSION, location, 4, 0.5D, 0.5D, 0.5D, 0.1D);
        location.getWorld().playSound(location, Sound.ENTITY_GENERIC_EXPLODE, 1.2F, 0.7F);
        radiationManager.createZone(location, "happy-ghast-bomber.radiation-zone");
    }

    @EventHandler
    public void onQuit(PlayerQuitEvent event) {
        ScheduledTask task = activeBombers.remove(event.getPlayer().getUniqueId());
        if (task != null) {
            task.cancel();
        }
    }

    private void dropProjectiles(Player player) {
        Location origin = player.getLocation().clone().add(0.0D, -1.0D, 0.0D);
        int amount = Math.max(1, plugin.getConfig().getInt("happy-ghast-bomber.projectile-count", 5));
        double spread = Math.max(0.0D, plugin.getConfig().getDouble("happy-ghast-bomber.projectile-spread", 2.0D));
        double downwardSpeed = Math.max(0.1D, plugin.getConfig().getDouble("happy-ghast-bomber.projectile-speed", 0.8D));

        for (int index = 0; index < amount; index++) {
            Location spawn = origin.clone().add(
                    ThreadLocalRandom.current().nextDouble(-spread, spread),
                    ThreadLocalRandom.current().nextDouble(-0.5D, 0.5D),
                    ThreadLocalRandom.current().nextDouble(-spread, spread)
            );
            Entity entity = origin.getWorld().spawnEntity(spawn, EntityType.WIND_CHARGE);
            if (entity instanceof Projectile projectile) {
                projectile.setShooter(player);
                projectile.setVelocity(new Vector(
                        ThreadLocalRandom.current().nextDouble(-0.15D, 0.15D),
                        -downwardSpeed,
                        ThreadLocalRandom.current().nextDouble(-0.15D, 0.15D)
                ));
                projectile.getPersistentDataContainer().set(bomberProjectileKey, PersistentDataType.BYTE, (byte) 1);
            }
            origin.getWorld().spawnParticle(Particle.SMOKE, spawn, 8, 0.2D, 0.2D, 0.2D, 0.02D);
        }
        origin.getWorld().playSound(origin, Sound.ENTITY_BREEZE_SHOOT, 1.0F, 0.6F);
    }

    private int countTnt(Player player) {
        int total = 0;
        for (ItemStack item : player.getInventory().getStorageContents()) {
            if (item != null && item.getType() == Material.TNT) {
                total += item.getAmount();
            }
        }
        return total;
    }

    private void removeTnt(Player player, int amount) {
        int remaining = amount;
        ItemStack[] contents = player.getInventory().getStorageContents();
        for (int slot = 0; slot < contents.length && remaining > 0; slot++) {
            ItemStack item = contents[slot];
            if (item == null || item.getType() != Material.TNT) {
                continue;
            }
            int taken = Math.min(remaining, item.getAmount());
            item.setAmount(item.getAmount() - taken);
            remaining -= taken;
            player.getInventory().setItem(slot, item.getAmount() <= 0 ? null : item);
        }
    }
}
