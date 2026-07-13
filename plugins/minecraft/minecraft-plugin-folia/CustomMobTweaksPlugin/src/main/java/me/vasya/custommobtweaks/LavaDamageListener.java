package me.vasya.custommobtweaks;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import org.bukkit.Bukkit;
import org.bukkit.Material;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.event.player.PlayerQuitEvent;
import org.bukkit.inventory.ItemStack;
import org.bukkit.inventory.PlayerInventory;
import org.bukkit.inventory.meta.Damageable;
import org.bukkit.inventory.meta.ItemMeta;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;

public final class LavaDamageListener implements Listener, PluginComponent {
    private final CustomMobTweaksPlugin plugin;
    private final Map<UUID, ScheduledTask> playerTasks = new ConcurrentHashMap<>();

    public LavaDamageListener(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
    }

    @Override
    public void start() {
        Bukkit.getPluginManager().registerEvents(this, plugin);
        for (Player player : Bukkit.getOnlinePlayers()) {
            schedule(player);
        }
    }

    @Override
    public void shutdown() {
        HandlerList.unregisterAll(this);
        for (ScheduledTask task : new ArrayList<>(playerTasks.values())) {
            if (task != null && !task.isCancelled()) {
                task.cancel();
            }
        }
        playerTasks.clear();
    }

    @EventHandler
    public void onJoin(PlayerJoinEvent event) {
        schedule(event.getPlayer());
    }

    @EventHandler
    public void onQuit(PlayerQuitEvent event) {
        ScheduledTask task = playerTasks.remove(event.getPlayer().getUniqueId());
        if (task != null) {
            task.cancel();
        }
    }

    private void schedule(Player player) {
        if (playerTasks.containsKey(player.getUniqueId())) {
            return;
        }
        int checkPeriod = Math.max(1, plugin.getConfig().getInt("lava-damage.check-period-ticks", 10));
        int[] lavaTicks = {0};

        ScheduledTask task = player.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
            if (!player.isOnline()) {
                playerTasks.remove(player.getUniqueId());
                scheduledTask.cancel();
                return;
            }
            if (!plugin.enabled("lava-damage")) {
                lavaTicks[0] = 0;
                return;
            }
            if (!isInLava(player)) {
                lavaTicks[0] = 0;
                return;
            }

            lavaTicks[0] += checkPeriod;
            boolean fullNetherite = hasFullNetheriteArmor(player);
            int armorInterval = Math.max(1, plugin.getConfig().getInt("lava-damage.armor-damage-interval-ticks", 20));
            if (lavaTicks[0] % armorInterval == 0) {
                if (fullNetherite) {
                    handleFullNetherite(player);
                } else {
                    damageRegularArmor(player);
                }
            }

            int meltInterval = Math.max(1, plugin.getConfig().getInt("lava-damage.netherite-item-melt-interval-ticks", 40));
            if (fullNetherite && lavaTicks[0] % meltInterval == 0) {
                meltRandomItem(player);
            }
            spawnParticles(player, fullNetherite);
        }, () -> playerTasks.remove(player.getUniqueId()), 1L, checkPeriod);
        if (task != null) {
            playerTasks.put(player.getUniqueId(), task);
        }
    }

    private boolean isInLava(Player player) {
        Material feet = player.getLocation().getBlock().getType();
        Material body = player.getLocation().clone().add(0.0D, 1.0D, 0.0D).getBlock().getType();
        return feet == Material.LAVA || body == Material.LAVA;
    }

    private void damageRegularArmor(Player player) {
        int amount = Math.max(0, plugin.getConfig().getInt("lava-damage.regular-armor-damage", 50));
        if (amount <= 0) {
            return;
        }
        PlayerInventory inventory = player.getInventory();
        ItemStack[] armor = inventory.getArmorContents();
        boolean changed = false;
        for (int index = 0; index < armor.length; index++) {
            ItemStack item = armor[index];
            if (item == null || item.getType().isAir() || isNetheriteArmor(item.getType())) {
                continue;
            }
            if (damageItem(item, amount)) {
                armor[index] = null;
                player.getWorld().playSound(player.getLocation(), Sound.ENTITY_ITEM_BREAK, 1.0F, 1.0F);
            }
            changed = true;
        }
        if (changed) {
            inventory.setArmorContents(armor);
        }
    }

    private void handleFullNetherite(Player player) {
        double healthDamage = Math.max(0.0D, plugin.getConfig().getDouble("lava-damage.netherite-health-damage", 4.0D));
        if (healthDamage > 0.0D) {
            player.damage(healthDamage);
        }

        int repair = Math.max(0, plugin.getConfig().getInt("lava-damage.netherite-repair-amount", 10));
        if (repair <= 0) {
            return;
        }
        ItemStack[] armor = player.getInventory().getArmorContents();
        for (ItemStack item : armor) {
            if (item == null || !isNetheriteArmor(item.getType())) {
                continue;
            }
            ItemMeta meta = item.getItemMeta();
            if (meta instanceof Damageable damageable) {
                damageable.setDamage(Math.max(0, damageable.getDamage() - repair));
                item.setItemMeta(meta);
            }
        }
        player.getInventory().setArmorContents(armor);
    }

    private boolean damageItem(ItemStack item, int amount) {
        ItemMeta meta = item.getItemMeta();
        if (!(meta instanceof Damageable damageable)) {
            return false;
        }
        int max = item.getType().getMaxDurability();
        int next = damageable.getDamage() + amount;
        if (max > 0 && next >= max) {
            return true;
        }
        damageable.setDamage(next);
        item.setItemMeta(meta);
        return false;
    }

    private void meltRandomItem(Player player) {
        List<Integer> candidates = new ArrayList<>();
        ItemStack[] contents = player.getInventory().getStorageContents();
        for (int slot = 0; slot < contents.length; slot++) {
            ItemStack item = contents[slot];
            if (item == null || item.getType().isAir() || isNetheriteItem(item.getType())) {
                continue;
            }
            candidates.add(slot);
        }
        if (candidates.isEmpty()) {
            return;
        }

        int slot = candidates.get(ThreadLocalRandom.current().nextInt(candidates.size()));
        ItemStack item = contents[slot];
        if (item == null) {
            return;
        }
        item.setAmount(item.getAmount() - 1);
        player.getInventory().setItem(slot, item.getAmount() <= 0 ? null : item);
        if (plugin.getConfig().getBoolean("lava-damage.notify-item-melt", true)) {
            player.sendMessage("§cЛава расплавила один предмет из инвентаря.");
        }
        player.getWorld().playSound(player.getLocation(), Sound.BLOCK_LAVA_EXTINGUISH, 0.8F, 0.7F);
    }

    private void spawnParticles(Player player, boolean netherite) {
        int density = Math.max(1, plugin.getConfig().getInt("lava-damage.particle-density", 15));
        player.getWorld().spawnParticle(
                netherite ? Particle.SOUL_FIRE_FLAME : Particle.FLAME,
                player.getLocation().add(0.0D, 1.0D, 0.0D),
                density,
                0.4D, 0.8D, 0.4D,
                0.02D
        );
    }

    private boolean hasFullNetheriteArmor(Player player) {
        ItemStack[] armor = player.getInventory().getArmorContents();
        if (armor.length < 4) {
            return false;
        }
        for (ItemStack item : armor) {
            if (item == null || !isNetheriteArmor(item.getType())) {
                return false;
            }
        }
        return true;
    }

    private boolean isNetheriteArmor(Material material) {
        return material == Material.NETHERITE_BOOTS
                || material == Material.NETHERITE_LEGGINGS
                || material == Material.NETHERITE_CHESTPLATE
                || material == Material.NETHERITE_HELMET;
    }

    private boolean isNetheriteItem(Material material) {
        return material.name().startsWith("NETHERITE_") || material == Material.NETHERITE_SCRAP;
    }
}
