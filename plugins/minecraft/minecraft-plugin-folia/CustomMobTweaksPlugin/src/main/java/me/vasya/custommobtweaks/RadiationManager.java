package me.vasya.custommobtweaks;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import org.bukkit.Bukkit;
import org.bukkit.Color;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.World;
import org.bukkit.entity.Player;
import org.bukkit.inventory.ItemStack;
import org.bukkit.util.Vector;

import java.util.ArrayList;
import java.util.List;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;

public final class RadiationManager implements PluginComponent {
    private final CustomMobTweaksPlugin plugin;
    private final Set<ScheduledTask> activeTasks = ConcurrentHashMap.newKeySet();

    public RadiationManager(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
    }

    public void createZone(Location location, String configPath) {
        World world = location.getWorld();
        if (world == null) {
            return;
        }

        Location center = location.clone();
        double radius = Math.max(0.5D, plugin.getConfig().getDouble(configPath + ".radius", 5.0D));
        double knockback = Math.max(0.0D, plugin.getConfig().getDouble(configPath + ".knockback", 2.0D));
        int fireTicks = Math.max(0, plugin.getConfig().getInt(configPath + ".fire-duration-ticks", 100));
        int duration = Math.max(1, plugin.getConfig().getInt(configPath + ".duration-ticks", 120));
        int period = Math.max(1, plugin.getConfig().getInt(configPath + ".check-period-ticks", 5));
        int itemDropInterval = Math.max(0, plugin.getConfig().getInt(configPath + ".item-drop-interval-ticks", 60));
        int density = Math.max(1, plugin.getConfig().getInt(configPath + ".particle-density", 50));
        boolean dropItems = plugin.getConfig().getBoolean(configPath + ".drop-random-inventory-items", true);
        int[] elapsed = {0};

        world.playSound(center, Sound.ENTITY_GENERIC_EXPLODE, 1.0F, 0.8F);
        world.spawnParticle(Particle.EXPLOSION, center, 3, 0.4D, 0.4D, 0.4D, 0.1D);

        ScheduledTask task = Bukkit.getRegionScheduler().runAtFixedRate(plugin, center, scheduledTask -> {
            if (elapsed[0] >= duration) {
                activeTasks.remove(scheduledTask);
                scheduledTask.cancel();
                return;
            }

            drawZone(center, radius, density);
            boolean shouldDropItem = dropItems
                    && itemDropInterval > 0
                    && elapsed[0] > 0
                    && elapsed[0] % itemDropInterval == 0;
            Location zoneSnapshot = center.clone();
            for (Player player : Bukkit.getOnlinePlayers()) {
                player.getScheduler().run(plugin, playerTask -> {
                    if (!player.isOnline() || !player.getWorld().equals(world)) {
                        return;
                    }
                    if (player.getLocation().distanceSquared(zoneSnapshot) > radius * radius) {
                        return;
                    }
                    applyZoneEffects(player, zoneSnapshot, knockback, fireTicks);
                    if (shouldDropItem) {
                        dropRandomItem(player);
                    }
                }, null);
            }
            elapsed[0] += period;
        }, 1L, period);
        if (task != null) {
            activeTasks.add(task);
        }
    }

    @Override
    public void shutdown() {
        for (ScheduledTask task : new ArrayList<>(activeTasks)) {
            if (task != null && !task.isCancelled()) {
                task.cancel();
            }
        }
        activeTasks.clear();
    }

    private void drawZone(Location center, double radius, int density) {
        World world = center.getWorld();
        if (world == null) {
            return;
        }
        Particle.DustOptions dust = new Particle.DustOptions(Color.fromRGB(120, 255, 40), 1.4F);
        for (int index = 0; index < density; index++) {
            double angle = ThreadLocalRandom.current().nextDouble(0.0D, Math.PI * 2.0D);
            double distance = Math.sqrt(ThreadLocalRandom.current().nextDouble()) * radius;
            double x = Math.cos(angle) * distance;
            double z = Math.sin(angle) * distance;
            double y = ThreadLocalRandom.current().nextDouble(0.05D, 1.4D);
            Location particle = center.clone().add(x, y, z);
            world.spawnParticle(Particle.DUST, particle, 1, 0.0D, 0.0D, 0.0D, 0.0D, dust);
            if (index % 4 == 0) {
                world.spawnParticle(Particle.SPORE_BLOSSOM_AIR, particle, 1, 0.1D, 0.1D, 0.1D, 0.0D);
            }
        }
    }

    private void applyZoneEffects(Player player, Location center, double knockback, int fireTicks) {
        Vector push = player.getLocation().toVector().subtract(center.toVector());
        if (push.lengthSquared() < 0.0001D) {
            push = new Vector(1.0D, 0.3D, 0.0D);
        }
        push.normalize().multiply(knockback);
        push.setY(Math.max(0.25D, push.getY()));
        player.setVelocity(player.getVelocity().add(push));
        if (fireTicks > 0) {
            player.setFireTicks(Math.max(player.getFireTicks(), fireTicks));
        }
    }

    private void dropRandomItem(Player player) {
        List<Integer> occupiedSlots = new ArrayList<>();
        ItemStack[] contents = player.getInventory().getStorageContents();
        for (int slot = 0; slot < contents.length; slot++) {
            ItemStack item = contents[slot];
            if (item != null && !item.getType().isAir()) {
                occupiedSlots.add(slot);
            }
        }
        if (occupiedSlots.isEmpty()) {
            return;
        }

        int slot = occupiedSlots.get(ThreadLocalRandom.current().nextInt(occupiedSlots.size()));
        ItemStack source = contents[slot];
        if (source == null || source.getType() == Material.AIR) {
            return;
        }
        ItemStack dropped = source.clone();
        dropped.setAmount(1);
        source.setAmount(source.getAmount() - 1);
        if (source.getAmount() <= 0) {
            player.getInventory().setItem(slot, null);
        } else {
            player.getInventory().setItem(slot, source);
        }
        player.getWorld().dropItemNaturally(player.getLocation(), dropped);
    }
}
