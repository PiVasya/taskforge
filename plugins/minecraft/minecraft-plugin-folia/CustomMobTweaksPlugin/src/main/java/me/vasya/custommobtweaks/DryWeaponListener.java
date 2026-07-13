package me.vasya.custommobtweaks;

import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.entity.Entity;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.block.Action;
import org.bukkit.event.inventory.CraftItemEvent;
import org.bukkit.event.player.PlayerInteractEvent;
import org.bukkit.inventory.EquipmentSlot;
import org.bukkit.inventory.ItemStack;
import org.bukkit.inventory.ShapedRecipe;
import org.bukkit.inventory.meta.Damageable;
import org.bukkit.inventory.meta.ItemMeta;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.potion.PotionEffect;
import org.bukkit.potion.PotionEffectType;
import org.bukkit.util.RayTraceResult;
import org.bukkit.util.Vector;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;

public final class DryWeaponListener implements Listener, PluginComponent {
    private final CustomMobTweaksPlugin plugin;
    private final NamespacedKey weaponKey;
    private final NamespacedKey durabilityKey;
    private final NamespacedKey fireRateKey;
    private final Map<UUID, Long> lastShotMillis = new ConcurrentHashMap<>();
    private final Map<UUID, HitWindow> leafHits = new ConcurrentHashMap<>();

    public DryWeaponListener(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
        this.weaponKey = new NamespacedKey(plugin, "dry_weapon");
        this.durabilityKey = new NamespacedKey(plugin, "dry_weapon_durability");
        this.fireRateKey = new NamespacedKey(plugin, "dry_weapon_fire_rate");
    }

    @Override
    public void start() {
        Bukkit.getPluginManager().registerEvents(this, plugin);
        registerRecipe();
    }

    @Override
    public void shutdown() {
        HandlerList.unregisterAll(this);
        lastShotMillis.clear();
        leafHits.clear();
    }

    @EventHandler(priority = EventPriority.HIGH, ignoreCancelled = true)
    public void onCraft(CraftItemEvent event) {
        ItemStack result = event.getRecipe().getResult();
        if (!isDryWeapon(result)) {
            return;
        }
        double failChance = normalizeChance(plugin.getConfig().getDouble("dry-weapon.craft-fail-chance", 0.05D));
        if (ThreadLocalRandom.current().nextDouble() < failChance) {
            event.setCancelled(true);
            event.getWhoClicked().sendMessage("§cСухое оружие развалилось во время сборки.");
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onUse(PlayerInteractEvent event) {
        if (!plugin.enabled("dry-weapon")
                || event.getHand() != EquipmentSlot.HAND
                || (event.getAction() != Action.RIGHT_CLICK_AIR && event.getAction() != Action.RIGHT_CLICK_BLOCK)) {
            return;
        }

        Player player = event.getPlayer();
        ItemStack weapon = player.getInventory().getItemInMainHand();
        if (!isDryWeapon(weapon)) {
            return;
        }
        event.setCancelled(true);

        int fireRate = getFireRate(weapon);
        long cooldownMillis = Math.max(1L, Math.round(1000.0D / Math.max(1, fireRate)));
        long now = System.currentTimeMillis();
        long last = lastShotMillis.getOrDefault(player.getUniqueId(), 0L);
        if (now - last < cooldownMillis) {
            return;
        }

        int minCost = Math.max(1, plugin.getConfig().getInt("dry-weapon.min-ammo-cost", 1));
        int maxCost = Math.max(minCost, plugin.getConfig().getInt("dry-weapon.max-ammo-cost", 10));
        int ammoCost = ThreadLocalRandom.current().nextInt(minCost, maxCost + 1);
        if (countMaterial(player, Material.LEAF_LITTER) < ammoCost) {
            player.sendMessage("§cНет боеприпасов: требуется сухая листва (Leaf Litter).");
            return;
        }

        removeMaterial(player, Material.LEAF_LITTER, ammoCost);
        lastShotMillis.put(player.getUniqueId(), now);
        shoot(player, weapon);
        damageWeapon(player, weapon);
    }

    private void registerRecipe() {
        NamespacedKey recipeKey = new NamespacedKey(plugin, "dry_weapon_recipe");
        Bukkit.removeRecipe(recipeKey);
        ShapedRecipe recipe = new ShapedRecipe(recipeKey, createWeapon());
        recipe.shape("CCC", "CLC", "CCC");
        recipe.setIngredient('C', Material.COPPER_BLOCK);
        recipe.setIngredient('L', Material.LEAF_LITTER);
        Bukkit.addRecipe(recipe);
    }

    private ItemStack createWeapon() {
        ItemStack item = new ItemStack(Material.CROSSBOW);
        ItemMeta meta = item.getItemMeta();
        if (meta == null) {
            return item;
        }
        int minRate = Math.max(1, plugin.getConfig().getInt("dry-weapon.min-fire-rate", 1));
        int maxRate = Math.max(minRate, plugin.getConfig().getInt("dry-weapon.max-fire-rate", 3));
        int fireRate = ThreadLocalRandom.current().nextInt(minRate, maxRate + 1);
        int durability = Math.max(1, plugin.getConfig().getInt("dry-weapon.weapon-durability", 150));

        meta.setDisplayName("§6§lСухое оружие");
        List<String> lore = new ArrayList<>();
        lore.add("§7Медное оружие, стреляющее сухой листвой");
        lore.add("§7Боеприпас: §eLeaf Litter");
        lore.add("§7Запас прочности: §e" + durability);
        lore.add("§7Скорострельность: §e" + fireRate + " выстр./сек");
        lore.add("");
        lore.add("§7Эффекты: торнадо, горящие листья,");
        lore.add("§7растительные занозы и повреждение предметов");
        meta.setLore(lore);
        meta.getPersistentDataContainer().set(weaponKey, PersistentDataType.BYTE, (byte) 1);
        meta.getPersistentDataContainer().set(durabilityKey, PersistentDataType.INTEGER, durability);
        meta.getPersistentDataContainer().set(fireRateKey, PersistentDataType.INTEGER, fireRate);
        item.setItemMeta(meta);
        return item;
    }

    private void shoot(Player shooter, ItemStack weapon) {
        Location eye = shooter.getEyeLocation();
        Vector direction = eye.getDirection().normalize();
        double maxRange = Math.max(1.0D, plugin.getConfig().getDouble("dry-weapon.max-range", 10.0D));
        double raySize = Math.max(0.1D, plugin.getConfig().getDouble("dry-weapon.ray-size", 0.45D));

        RayTraceResult result = shooter.getWorld().rayTraceEntities(
                eye,
                direction,
                maxRange,
                raySize,
                entity -> entity instanceof LivingEntity && entity != shooter
        );
        Location end = eye.clone().add(direction.clone().multiply(maxRange));
        LivingEntity target = null;
        if (result != null) {
            end = result.getHitPosition().toLocation(shooter.getWorld());
            if (result.getHitEntity() instanceof LivingEntity living) {
                target = living;
            }
        }

        spawnLeafTrail(eye, end);
        shooter.getWorld().playSound(eye, Sound.ENTITY_BREEZE_SHOOT, 0.8F, 1.8F);
        if (target == null) {
            return;
        }

        double distance = eye.distance(target.getLocation());
        double damage = distance > 5.0D
                ? plugin.getConfig().getDouble("dry-weapon.long-range-damage", 2.0D)
                : plugin.getConfig().getDouble("dry-weapon.short-range-damage", 1.0D);
        if (damage > 0.0D) {
            target.damage(damage, shooter);
        }
        if (target instanceof Player playerTarget) {
            trackLeafHit(playerTarget);
            applySpecialEffects(playerTarget);
        }
    }

    private void spawnLeafTrail(Location start, Location end) {
        Vector delta = end.toVector().subtract(start.toVector());
        double length = delta.length();
        if (length < 0.001D) {
            return;
        }
        Vector step = delta.normalize().multiply(0.35D);
        Location cursor = start.clone();
        int points = Math.max(1, (int) (length / 0.35D));
        for (int index = 0; index < points; index++) {
            cursor.add(step);
            start.getWorld().spawnParticle(Particle.FALLING_SPORE_BLOSSOM, cursor, 1, 0.05D, 0.05D, 0.05D, 0.0D);
            if (index % 3 == 0) {
                start.getWorld().spawnParticle(Particle.HAPPY_VILLAGER, cursor, 1, 0.05D, 0.05D, 0.05D, 0.0D);
            }
        }
    }

    private void trackLeafHit(Player target) {
        long now = System.currentTimeMillis();
        long durationMillis = Math.max(1L, plugin.getConfig().getLong("dry-weapon.leaf-hit-tracking-duration-ticks", 1200L)) * 50L;
        HitWindow window = leafHits.compute(target.getUniqueId(), (uuid, previous) -> {
            if (previous == null || now - previous.firstHitMillis > durationMillis) {
                return new HitWindow(now, 1);
            }
            return new HitWindow(previous.firstHitMillis, previous.hits + 1);
        });

        int threshold = Math.max(1, plugin.getConfig().getInt("dry-weapon.tornado-minimum-hits", 5));
        double chance = normalizeChance(plugin.getConfig().getDouble("dry-weapon.tornado-chance", 0.2D));
        if (window.hits >= threshold && ThreadLocalRandom.current().nextDouble() < chance) {
            activateTornado(target, window.hits);
            leafHits.remove(target.getUniqueId());
        }
    }

    private void activateTornado(Player target, int hits) {
        int perTwoHits = Math.max(1, plugin.getConfig().getInt("dry-weapon.tornado-lift-per-2-hits", 1));
        int maxBlocks = Math.max(1, plugin.getConfig().getInt("dry-weapon.tornado-max-lift-blocks", 50));
        int lift = Math.min(maxBlocks, Math.max(1, (hits / 2) * perTwoHits));
        int ticks = Math.max(10, lift * 2);
        int[] elapsed = {0};

        target.getScheduler().runAtFixedRate(plugin, task -> {
            if (!target.isOnline() || target.isDead() || elapsed[0] >= ticks) {
                task.cancel();
                return;
            }
            Vector velocity = target.getVelocity();
            velocity.setY(Math.max(0.35D, Math.min(1.0D, 0.35D + lift * 0.01D)));
            velocity.setX(velocity.getX() + Math.sin(elapsed[0] * 0.5D) * 0.08D);
            velocity.setZ(velocity.getZ() + Math.cos(elapsed[0] * 0.5D) * 0.08D);
            target.setVelocity(velocity);
            target.getWorld().spawnParticle(Particle.GUST, target.getLocation(), 6, 0.5D, 0.3D, 0.5D, 0.05D);
            elapsed[0]++;
        }, null, 1L, 1L);
    }

    private void applySpecialEffects(Player target) {
        if (ThreadLocalRandom.current().nextDouble() < normalizeChance(plugin.getConfig().getDouble("dry-weapon.fire-effect-chance", 0.2D))) {
            target.setFireTicks(Math.max(target.getFireTicks(), plugin.getConfig().getInt("dry-weapon.fire-effect-duration-ticks", 60)));
        }
        if (ThreadLocalRandom.current().nextDouble() < normalizeChance(plugin.getConfig().getDouble("dry-weapon.splinter-effect-chance", 0.2D))) {
            int duration = Math.max(1, plugin.getConfig().getInt("dry-weapon.splinter-effect-duration-ticks", 100));
            target.addPotionEffect(new PotionEffect(PotionEffectType.POISON, duration, 0, true, true, true));
            target.addPotionEffect(new PotionEffect(PotionEffectType.WEAKNESS, duration, 0, true, true, true));
            target.sendMessage("§aРастительные занозы впились в вас.");
        }
        if (ThreadLocalRandom.current().nextDouble() < normalizeChance(plugin.getConfig().getDouble("dry-weapon.durability-damage-chance", 0.2D))) {
            damageRandomInventoryItem(target, Math.max(1, plugin.getConfig().getInt("dry-weapon.item-durability-damage", 5)));
        }
    }

    private void damageRandomInventoryItem(Player target, int amount) {
        List<Integer> candidates = new ArrayList<>();
        ItemStack[] contents = target.getInventory().getContents();
        for (int slot = 0; slot < contents.length; slot++) {
            ItemStack item = contents[slot];
            if (item != null && item.getItemMeta() instanceof Damageable) {
                candidates.add(slot);
            }
        }
        if (candidates.isEmpty()) {
            return;
        }
        int slot = candidates.get(ThreadLocalRandom.current().nextInt(candidates.size()));
        ItemStack item = contents[slot];
        if (item == null) {
            return;
        }
        ItemMeta meta = item.getItemMeta();
        if (!(meta instanceof Damageable damageable)) {
            return;
        }
        int next = damageable.getDamage() + amount;
        int max = item.getType().getMaxDurability();
        if (max > 0 && next >= max) {
            target.getInventory().setItem(slot, null);
            target.getWorld().playSound(target.getLocation(), Sound.ENTITY_ITEM_BREAK, 1.0F, 1.0F);
        } else {
            damageable.setDamage(next);
            item.setItemMeta(meta);
            target.getInventory().setItem(slot, item);
        }
    }

    private void damageWeapon(Player player, ItemStack weapon) {
        ItemMeta meta = weapon.getItemMeta();
        if (meta == null) {
            return;
        }
        Integer durability = meta.getPersistentDataContainer().get(durabilityKey, PersistentDataType.INTEGER);
        int next = (durability == null ? plugin.getConfig().getInt("dry-weapon.weapon-durability", 150) : durability) - 1;
        if (next <= 0) {
            player.getInventory().setItemInMainHand(null);
            player.getWorld().playSound(player.getLocation(), Sound.ENTITY_ITEM_BREAK, 1.0F, 0.8F);
            return;
        }
        meta.getPersistentDataContainer().set(durabilityKey, PersistentDataType.INTEGER, next);
        if (meta instanceof Damageable damageable) {
            int maxDamage = weapon.getType().getMaxDurability();
            int configured = Math.max(1, plugin.getConfig().getInt("dry-weapon.weapon-durability", 150));
            damageable.setDamage(Math.min(maxDamage - 1, (int) Math.round(maxDamage * (1.0D - next / (double) configured))));
        }
        weapon.setItemMeta(meta);
        player.getInventory().setItemInMainHand(weapon);
    }

    private boolean isDryWeapon(ItemStack item) {
        if (item == null || item.getType() != Material.CROSSBOW || !item.hasItemMeta()) {
            return false;
        }
        Byte marker = item.getItemMeta().getPersistentDataContainer().get(weaponKey, PersistentDataType.BYTE);
        return marker != null && marker == (byte) 1;
    }

    private int getFireRate(ItemStack item) {
        Integer value = item.getItemMeta().getPersistentDataContainer().get(fireRateKey, PersistentDataType.INTEGER);
        return value == null ? 1 : Math.max(1, value);
    }

    private int countMaterial(Player player, Material material) {
        int total = 0;
        for (ItemStack item : player.getInventory().getStorageContents()) {
            if (item != null && item.getType() == material) {
                total += item.getAmount();
            }
        }
        return total;
    }

    private void removeMaterial(Player player, Material material, int amount) {
        int remaining = amount;
        ItemStack[] contents = player.getInventory().getStorageContents();
        for (int slot = 0; slot < contents.length && remaining > 0; slot++) {
            ItemStack item = contents[slot];
            if (item == null || item.getType() != material) {
                continue;
            }
            int removed = Math.min(item.getAmount(), remaining);
            item.setAmount(item.getAmount() - removed);
            remaining -= removed;
            player.getInventory().setItem(slot, item.getAmount() <= 0 ? null : item);
        }
    }

    private double normalizeChance(double value) {
        if (value > 1.0D) {
            value /= 100.0D;
        }
        return Math.max(0.0D, Math.min(1.0D, value));
    }

    private record HitWindow(long firstHitMillis, int hits) {
    }
}
