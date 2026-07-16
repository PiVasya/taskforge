package me.vasya.custommobtweaks;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import org.bukkit.Bukkit;
import org.bukkit.Color;
import org.bukkit.EntityEffect;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.Particle;
import org.bukkit.Sound;
import org.bukkit.World;
import org.bukkit.attribute.Attribute;
import org.bukkit.attribute.AttributeInstance;
import org.bukkit.configuration.ConfigurationSection;
import org.bukkit.enchantments.Enchantment;
import org.bukkit.entity.AbstractArrow;
import org.bukkit.entity.AbstractSkeleton;
import org.bukkit.entity.Armadillo;
import org.bukkit.entity.Bogged;
import org.bukkit.entity.Breeze;
import org.bukkit.entity.Drowned;
import org.bukkit.entity.Entity;
import org.bukkit.entity.EntityType;
import org.bukkit.entity.Illusioner;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Mob;
import org.bukkit.entity.Monster;
import org.bukkit.entity.Pillager;
import org.bukkit.entity.Player;
import org.bukkit.entity.Projectile;
import org.bukkit.entity.Snowman;
import org.bukkit.entity.Stray;
import org.bukkit.entity.Trident;
import org.bukkit.entity.WindCharge;
import org.bukkit.entity.WitherSkeleton;
import org.bukkit.entity.Zombie;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.CreatureSpawnEvent;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.event.entity.EntityDamageEvent;
import org.bukkit.event.entity.EntityDeathEvent;
import org.bukkit.event.entity.EntitySpawnEvent;
import org.bukkit.event.entity.EntityTargetLivingEntityEvent;
import org.bukkit.event.entity.ProjectileHitEvent;
import org.bukkit.event.entity.ProjectileLaunchEvent;
import org.bukkit.inventory.EntityEquipment;
import org.bukkit.inventory.ItemStack;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.potion.PotionEffect;
import org.bukkit.potion.PotionEffectType;
import org.bukkit.projectiles.ProjectileSource;
import org.bukkit.util.Vector;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadLocalRandom;

public final class LegacyEnhancementsListener implements Listener, PluginComponent {
    private final CustomMobTweaksPlugin plugin;
    private final NamespacedKey tridentZombieKey;
    private final NamespacedKey boggedBuffKey;
    private final NamespacedKey originalScaleKey;
    private final NamespacedKey harderBreezeChargeKey;
    private final NamespacedKey tridentZombieProjectileKey;
    private final NamespacedKey lootShooterUuidKey;
    private final NamespacedKey lootShooterNameKey;
    private final NamespacedKey lootShooterLootingKey;
    private final NamespacedKey illusionerCloneKey;

    private final Set<UUID> breezeTasks = ConcurrentHashMap.newKeySet();
    private final Set<UUID> creakingTasks = ConcurrentHashMap.newKeySet();
    private final Map<UUID, ScheduledTask> projectileRainTasks = new ConcurrentHashMap<>();
    private final Map<UUID, ScheduledTask> bleedingTasks = new ConcurrentHashMap<>();
    private final Map<UUID, ScheduledTask> buffRestoreTasks = new ConcurrentHashMap<>();
    private final Map<UUID, ScheduledTask> strayFreezeTasks = new ConcurrentHashMap<>();
    private final Map<UUID, Double> strayOriginalMaxHealth = new ConcurrentHashMap<>();
    private final Map<UUID, ScheduledTask> armadilloSphereTasks = new ConcurrentHashMap<>();
    private final Map<UUID, ScheduledTask> tridentZombieTasks = new ConcurrentHashMap<>();
    private final Map<UUID, LootAttribution> lastPlayerDamage = new ConcurrentHashMap<>();

    private static final long LOOT_ATTRIBUTION_TTL_MILLIS = 30_000L;

    public LegacyEnhancementsListener(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
        this.tridentZombieKey = new NamespacedKey(plugin, "trident_zombie");
        this.boggedBuffKey = new NamespacedKey(plugin, "bogged_buffed");
        this.originalScaleKey = new NamespacedKey(plugin, "bogged_original_scale");
        this.harderBreezeChargeKey = new NamespacedKey(plugin, "harder_breeze_charge");
        this.tridentZombieProjectileKey = new NamespacedKey(plugin, "trident_zombie_projectile");
        this.lootShooterUuidKey = new NamespacedKey(plugin, "loot_shooter_uuid");
        this.lootShooterNameKey = new NamespacedKey(plugin, "loot_shooter_name");
        this.lootShooterLootingKey = new NamespacedKey(plugin, "loot_shooter_looting");
        this.illusionerCloneKey = new NamespacedKey(plugin, "illusioner_clone");
    }

    @Override
    public void start() {
        Bukkit.getPluginManager().registerEvents(this, plugin);
    }

    @Override
    public void shutdown() {
        HandlerList.unregisterAll(this);
        cancelAll(projectileRainTasks);
        cancelAll(bleedingTasks);
        cancelAll(buffRestoreTasks);
        cancelAll(strayFreezeTasks);
        cancelAll(armadilloSphereTasks);
        cancelAll(tridentZombieTasks);
        breezeTasks.clear();
        creakingTasks.clear();
        strayOriginalMaxHealth.clear();
        lastPlayerDamage.clear();
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onSpawn(EntitySpawnEvent event) {
        Entity entity = event.getEntity();
        if (entity instanceof Breeze breeze && plugin.enabled("harder-breeze")) {
            startBreezeAttackTask(breeze);
        }
        if (entity.getType() == EntityType.CREAKING && entity instanceof LivingEntity living && plugin.enabled("harder-creaking")) {
            enhanceCreaking(living);
        }
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onCreatureSpawn(CreatureSpawnEvent event) {
        if (plugin.enabled("trident-zombie") && event.getEntity() instanceof Zombie zombie) {
            maybeMakeTridentZombie(zombie, event.getSpawnReason());
        }
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onTarget(EntityTargetLivingEntityEvent event) {
        if (event.getEntity() instanceof Breeze breeze && plugin.enabled("harder-breeze")) {
            startBreezeAttackTask(breeze);
        }
        if (event.getEntity().getType() == EntityType.CREAKING
                && event.getEntity() instanceof LivingEntity living
                && plugin.enabled("harder-creaking")) {
            enhanceCreaking(living);
        }
        if (event.getEntity() instanceof Zombie zombie
                && zombie.getPersistentDataContainer().has(tridentZombieKey, PersistentDataType.BYTE)
                && plugin.enabled("trident-zombie")) {
            startTridentZombieAttackTask(zombie);
        }
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onDamageByEntity(EntityDamageByEntityEvent event) {
        if (!(event.getEntity() instanceof LivingEntity target)) {
            return;
        }

        if (plugin.enabled("harder-creaking")) {
            handleCreakingHit(event, target);
        }
        if (plugin.enabled("harder-breeze")) {
            handleBreezeDamage(event, target);
        }
        if (plugin.enabled("harder-bogged") && target instanceof Player player) {
            handleBoggedHit(event.getDamager(), player);
        }
        if (plugin.enabled("harder-stray") && target instanceof Player player) {
            handleStrayHit(event.getDamager(), player);
        }
        if (event.getDamager() instanceof Trident trident
                && trident.getPersistentDataContainer().has(tridentZombieProjectileKey, PersistentDataType.BYTE)) {
            double damage = Math.max(0.0D, plugin.getConfig().getDouble("trident-zombie.trident-damage", 8.0D));
            event.setDamage(damage);
        }
        if (event.getDamager() instanceof Mob mob
                && mob.getPersistentDataContainer().has(boggedBuffKey, PersistentDataType.BYTE)) {
            double multiplier = plugin.getConfig().getDouble("harder-bogged.mob-damage-multiplier", 1.5D);
            event.setDamage(event.getDamage() * Math.max(0.0D, multiplier));
        }
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onLootAttributionDamage(EntityDamageByEntityEvent event) {
        if (event.getEntity() instanceof LivingEntity target) {
            rememberLootAttribution(event, target);
        }
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onArmadilloDamage(EntityDamageEvent event) {
        if (!plugin.enabled("harder-armadillo") || !(event.getEntity() instanceof Armadillo armadillo)) {
            return;
        }
        startArmadilloSphere(armadillo, event.getFinalDamage());
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onProjectileLaunch(ProjectileLaunchEvent event) {
        Projectile projectile = event.getEntity();
        if (!(projectile.getShooter() instanceof Player player)) {
            return;
        }
        int looting = player.getInventory().getItemInMainHand().getEnchantmentLevel(Enchantment.LOOTING);
        projectile.getPersistentDataContainer().set(
                lootShooterUuidKey,
                PersistentDataType.STRING,
                player.getUniqueId().toString()
        );
        projectile.getPersistentDataContainer().set(
                lootShooterNameKey,
                PersistentDataType.STRING,
                player.getName()
        );
        projectile.getPersistentDataContainer().set(
                lootShooterLootingKey,
                PersistentDataType.INTEGER,
                Math.max(0, looting)
        );
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onProjectileHit(ProjectileHitEvent event) {
        if (!plugin.enabled("harder-breeze")) {
            return;
        }
        Projectile projectile = event.getEntity();
        ProjectileSource shooter = projectile.getShooter();
        if (!(shooter instanceof Breeze)) {
            return;
        }

        Location impact = projectile.getLocation().clone();
        createBreezeExplosion(impact, projectile);

    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onDeath(EntityDeathEvent event) {
        LivingEntity entity = event.getEntity();
        if (!entity.getPersistentDataContainer().has(illusionerCloneKey, PersistentDataType.BYTE)) {
            addConfiguredLoot(event);
        }
        cleanupEntity(entity);
    }

    private void enhanceCreaking(LivingEntity creaking) {
        if (!creakingTasks.add(creaking.getUniqueId())) {
            return;
        }

        int amplifier = Math.max(0, plugin.getConfig().getInt("harder-creaking.speed-amplifier", 9));
        creaking.addPotionEffect(new PotionEffect(PotionEffectType.SPEED, Integer.MAX_VALUE, amplifier, false, false, false));

        long period = Math.max(1L, plugin.getConfig().getLong("harder-creaking.darkness-check-period-ticks", 20L));
        ScheduledTask creakingTask = creaking.getScheduler().runAtFixedRate(plugin, task -> {
            if (!creaking.isValid() || creaking.isDead() || !plugin.enabled("harder-creaking")) {
                creakingTasks.remove(creaking.getUniqueId());
                task.cancel();
                return;
            }

            double radius = Math.max(0.0D, plugin.getConfig().getDouble("harder-creaking.darkness-radius", 10.0D));
            int duration = Math.max(1, plugin.getConfig().getInt("harder-creaking.darkness-duration-ticks", 50));
            Location center = creaking.getLocation().clone();
            World world = creaking.getWorld();
            for (Player player : Bukkit.getOnlinePlayers()) {
                player.getScheduler().run(plugin, playerTask -> {
                    if (!player.isOnline() || !player.getWorld().equals(world)) {
                        return;
                    }
                    if (player.getLocation().distanceSquared(center) <= radius * radius) {
                        player.addPotionEffect(new PotionEffect(PotionEffectType.DARKNESS, duration, 0, true, true, true));
                    }
                }, null);
            }
        }, () -> creakingTasks.remove(creaking.getUniqueId()), 1L, period);
        if (creakingTask == null) {
            creakingTasks.remove(creaking.getUniqueId());
        }
    }

    private void handleCreakingHit(EntityDamageByEntityEvent event, LivingEntity target) {
        if (event.getDamager().getType() != EntityType.CREAKING || !(target instanceof Player player)) {
            return;
        }

        double damage = plugin.getConfig().getDouble("harder-creaking.hit-damage", 20.0D);
        if (damage >= 0.0D) {
            event.setDamage(damage);
        }
        int blindnessTicks = Math.max(0, plugin.getConfig().getInt("harder-creaking.blindness-duration-ticks", 100));
        if (blindnessTicks > 0) {
            player.addPotionEffect(new PotionEffect(PotionEffectType.BLINDNESS, blindnessTicks, 0, true, true, true));
        }
    }

    private void startBreezeAttackTask(Breeze breeze) {
        if (!breezeTasks.add(breeze.getUniqueId())) {
            return;
        }

        long cooldown = Math.max(1L, plugin.getConfig().getLong("harder-breeze.attack-cooldown-ticks", 10L));
        ScheduledTask breezeTask = breeze.getScheduler().runAtFixedRate(plugin, task -> {
            if (!breeze.isValid() || breeze.isDead() || !plugin.enabled("harder-breeze")) {
                breezeTasks.remove(breeze.getUniqueId());
                task.cancel();
                return;
            }

            Player target = findBreezeTarget(breeze);
            if (target != null) {
                shootBreezeCharge(breeze, target);
            }
        }, () -> breezeTasks.remove(breeze.getUniqueId()), 1L, cooldown);
        if (breezeTask == null) {
            breezeTasks.remove(breeze.getUniqueId());
        }
    }

    private Player findBreezeTarget(Breeze breeze) {
        double range = Math.max(1.0D, plugin.getConfig().getDouble("harder-breeze.attack-range", 16.0D));
        if (breeze.getTarget() instanceof Player current
                && current.isValid()
                && !current.isDead()
                && current.getWorld().equals(breeze.getWorld())
                && current.getLocation().distanceSquared(breeze.getLocation()) <= range * range
                && (!plugin.getConfig().getBoolean("harder-breeze.require-line-of-sight", true) || breeze.hasLineOfSight(current))) {
            return current;
        }

        Player nearest = null;
        double nearestDistance = range * range;
        for (Entity entity : breeze.getNearbyEntities(range, range, range)) {
            if (!(entity instanceof Player player) || player.isDead()) {
                continue;
            }
            if (plugin.getConfig().getBoolean("harder-breeze.require-line-of-sight", true) && !breeze.hasLineOfSight(player)) {
                continue;
            }
            double distance = player.getLocation().distanceSquared(breeze.getLocation());
            if (distance < nearestDistance) {
                nearestDistance = distance;
                nearest = player;
            }
        }
        return nearest;
    }

    private void shootBreezeCharge(Breeze breeze, Player target) {
        Location origin = breeze.getEyeLocation().clone();
        Vector direction = target.getEyeLocation().toVector().subtract(origin.toVector());
        if (direction.lengthSquared() < 0.0001D) {
            return;
        }
        direction.normalize().multiply(Math.max(0.1D, plugin.getConfig().getDouble("harder-breeze.projectile-speed", 1.5D)));

        Entity spawned = breeze.getWorld().spawnEntity(origin, EntityType.WIND_CHARGE);
        if (spawned instanceof Projectile projectile) {
            projectile.setShooter(breeze);
            projectile.setVelocity(direction);
            projectile.getPersistentDataContainer().set(harderBreezeChargeKey, PersistentDataType.BYTE, (byte) 1);
        }
        breeze.getWorld().playSound(origin, Sound.ENTITY_BREEZE_SHOOT, 1.0F, 1.0F);
    }

    private void handleBreezeDamage(EntityDamageByEntityEvent event, LivingEntity target) {
        if (!(target instanceof Player player)) {
            return;
        }

        boolean breezeAttack = event.getDamager() instanceof Breeze;
        if (event.getDamager() instanceof Projectile projectile && projectile.getShooter() instanceof Breeze) {
            breezeAttack = true;
        }
        if (!breezeAttack) {
            return;
        }
        startProjectileRain(player);
    }

    private void startProjectileRain(Player player) {
        UUID uuid = player.getUniqueId();
        ScheduledTask previous = projectileRainTasks.remove(uuid);
        if (previous != null) {
            previous.cancel();
        }

        int duration = Math.max(1, plugin.getConfig().getInt("harder-breeze.projectile-rain-duration-ticks", 60));
        int period = Math.max(1, plugin.getConfig().getInt("harder-breeze.projectile-rain-period-ticks", 10));
        int[] elapsed = {0};

        ScheduledTask task = player.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
            if (!player.isOnline() || player.isDead() || elapsed[0] >= duration || !plugin.enabled("harder-breeze")) {
                projectileRainTasks.remove(uuid);
                scheduledTask.cancel();
                return;
            }
            spawnProjectileRainWave(player);
            elapsed[0] += period;
        }, () -> projectileRainTasks.remove(uuid), 1L, period);
        if (task != null) {
            projectileRainTasks.put(uuid, task);
        }
    }

    private void spawnProjectileRainWave(Player player) {
        Location center = player.getLocation().clone();
        int amount = Math.max(0, plugin.getConfig().getInt("harder-breeze.projectile-rain-amount", 5));
        double radius = Math.max(0.0D, plugin.getConfig().getDouble("harder-breeze.projectile-rain-radius", 3.0D));
        double height = Math.max(1.0D, plugin.getConfig().getDouble("harder-breeze.projectile-rain-height", 8.0D));
        double speed = Math.max(0.05D, plugin.getConfig().getDouble("harder-breeze.projectile-rain-speed", 0.5D));

        for (int index = 0; index < amount; index++) {
            double x = ThreadLocalRandom.current().nextDouble(-radius, radius);
            double z = ThreadLocalRandom.current().nextDouble(-radius, radius);
            Location spawn = center.clone().add(x, height, z);
            Entity entity = center.getWorld().spawnEntity(spawn, EntityType.WIND_CHARGE);
            if (entity instanceof Projectile projectile) {
                Vector velocity = new Vector(
                        ThreadLocalRandom.current().nextDouble(-0.1D, 0.1D),
                        -1.0D,
                        ThreadLocalRandom.current().nextDouble(-0.1D, 0.1D)
                ).normalize().multiply(speed);
                projectile.setVelocity(velocity);
            }
            center.getWorld().spawnParticle(Particle.CLOUD, spawn, 10, 0.2D, 0.2D, 0.2D, 0.02D);
            center.getWorld().playSound(spawn, Sound.ENTITY_BREEZE_SHOOT, 0.5F, 1.5F);
        }
    }

    private void createBreezeExplosion(Location impact, Projectile projectile) {
        World world = impact.getWorld();
        if (world == null) {
            return;
        }
        world.spawnParticle(Particle.EXPLOSION, impact, 3, 0.5D, 0.5D, 0.5D, 0.1D);
        world.spawnParticle(Particle.CLOUD, impact, 20, 1.0D, 1.0D, 1.0D, 0.1D);
        world.spawnParticle(Particle.GUST, impact, 15, 1.0D, 1.0D, 1.0D, 0.2D);
        world.playSound(impact, Sound.ENTITY_GENERIC_EXPLODE, 1.0F, 1.2F);

        double radius = Math.max(0.0D, plugin.getConfig().getDouble("harder-breeze.explosion-radius", 3.0D));
        double force = Math.max(0.0D, plugin.getConfig().getDouble("harder-breeze.explosion-knockback", 1.5D));
        double damage = Math.max(0.0D, plugin.getConfig().getDouble("harder-breeze.explosion-damage", 0.0D));

        for (Entity nearby : projectile.getNearbyEntities(radius, radius, radius)) {
            if (!(nearby instanceof LivingEntity living)) {
                continue;
            }
            Vector away = living.getLocation().toVector().subtract(impact.toVector());
            if (away.lengthSquared() < 0.0001D) {
                away = new Vector(0.0D, 1.0D, 0.0D);
            }
            away.normalize().multiply(force);
            away.setY(Math.max(0.3D, away.getY()));
            living.setVelocity(living.getVelocity().add(away));
            if (damage > 0.0D) {
                living.damage(damage, projectile);
            }
        }
    }

    private void handleBoggedHit(Entity damager, Player player) {
        boolean fromBogged = damager instanceof Bogged;
        if (damager instanceof Projectile projectile && projectile.getShooter() instanceof Bogged) {
            fromBogged = true;
        }
        if (!fromBogged) {
            return;
        }
        startBleeding(player);
    }

    private void startBleeding(Player player) {
        UUID uuid = player.getUniqueId();
        ScheduledTask previous = bleedingTasks.remove(uuid);
        if (previous != null) {
            previous.cancel();
        }

        int duration = Math.max(1, plugin.getConfig().getInt("harder-bogged.bleeding-duration-ticks", 100));
        int interval = Math.max(1, plugin.getConfig().getInt("harder-bogged.particle-spawn-interval-ticks", 5));
        int poisonDuration = Math.max(0, plugin.getConfig().getInt("harder-bogged.poison-duration-ticks", 20));
        if (poisonDuration > 0) {
            player.addPotionEffect(new PotionEffect(PotionEffectType.POISON, poisonDuration, 0, false, true, true));
        }
        int[] elapsed = {0};

        ScheduledTask task = player.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
            if (!player.isOnline() || player.isDead() || elapsed[0] >= duration || !plugin.enabled("harder-bogged")) {
                bleedingTasks.remove(uuid);
                scheduledTask.cancel();
                return;
            }
            createBloodPuddle(player.getLocation());
            elapsed[0] += interval;
        }, () -> bleedingTasks.remove(uuid), 1L, interval);
        if (task != null) {
            bleedingTasks.put(uuid, task);
        }
    }

    private void createBloodPuddle(Location location) {
        Location puddle = location.clone();
        puddle.setY(Math.floor(puddle.getY()) + 0.1D);
        int duration = Math.max(1, plugin.getConfig().getInt("harder-bogged.blood-particle-duration-ticks", 100));
        int checkPeriod = Math.max(1, plugin.getConfig().getInt("harder-bogged.puddle-check-period-ticks", 5));
        double triggerRadius = Math.max(0.2D, plugin.getConfig().getDouble("harder-bogged.puddle-trigger-radius", 1.0D));
        int[] elapsed = {0};

        Bukkit.getRegionScheduler().runAtFixedRate(plugin, puddle, task -> {
            World world = puddle.getWorld();
            if (world == null || elapsed[0] >= duration || !plugin.enabled("harder-bogged")) {
                task.cancel();
                return;
            }

            Particle.DustOptions green = new Particle.DustOptions(Color.fromRGB(0, 255, 0), 1.5F);
            world.spawnParticle(Particle.DUST, puddle, 10, 0.3D, 0.05D, 0.3D, 0.0D, green);
            world.spawnParticle(Particle.ITEM_SLIME, puddle, 5, 0.2D, 0.05D, 0.2D, 0.0D);

            for (Entity nearby : world.getNearbyEntities(puddle, triggerRadius, 1.5D, triggerRadius)) {
                if (nearby instanceof Mob mob && !(mob instanceof Bogged)) {
                    applyBoggedBuff(mob);
                    task.cancel();
                    return;
                }
            }
            elapsed[0] += checkPeriod;
        }, 1L, checkPeriod);
    }

    private void applyBoggedBuff(Mob mob) {
        UUID uuid = mob.getUniqueId();
        ScheduledTask oldRestore = buffRestoreTasks.remove(uuid);
        if (oldRestore != null) {
            oldRestore.cancel();
        }

        AttributeInstance scale = mob.getAttribute(Attribute.SCALE);
        if (!mob.getPersistentDataContainer().has(boggedBuffKey, PersistentDataType.BYTE)) {
            mob.getPersistentDataContainer().set(boggedBuffKey, PersistentDataType.BYTE, (byte) 1);
            if (scale != null) {
                mob.getPersistentDataContainer().set(originalScaleKey, PersistentDataType.DOUBLE, scale.getBaseValue());
                double multiplier = Math.max(0.1D, plugin.getConfig().getDouble("harder-bogged.mob-size-multiplier", 1.25D));
                scale.setBaseValue(scale.getBaseValue() * multiplier);
            }
        }

        AttributeInstance maxHealth = mob.getAttribute(Attribute.MAX_HEALTH);
        if (maxHealth != null) {
            mob.setHealth(maxHealth.getValue());
        }
        mob.getWorld().spawnParticle(Particle.HAPPY_VILLAGER, mob.getLocation().add(0.0D, mob.getHeight() * 0.5D, 0.0D), 20, 0.4D, 0.5D, 0.4D, 0.05D);

        long duration = Math.max(1L, plugin.getConfig().getLong("harder-bogged.mob-buff-duration-ticks", 200L));
        ScheduledTask restore = mob.getScheduler().runDelayed(plugin, task -> restoreBoggedBuff(mob), null, duration);
        if (restore != null) {
            buffRestoreTasks.put(uuid, restore);
        }
    }

    private void restoreBoggedBuff(Mob mob) {
        UUID uuid = mob.getUniqueId();
        buffRestoreTasks.remove(uuid);
        if (!mob.isValid()) {
            return;
        }
        Double original = mob.getPersistentDataContainer().get(originalScaleKey, PersistentDataType.DOUBLE);
        AttributeInstance scale = mob.getAttribute(Attribute.SCALE);
        if (original != null && scale != null) {
            scale.setBaseValue(original);
        }
        mob.getPersistentDataContainer().remove(originalScaleKey);
        mob.getPersistentDataContainer().remove(boggedBuffKey);
        mob.getWorld().spawnParticle(Particle.SMOKE, mob.getLocation().add(0.0D, mob.getHeight() * 0.5D, 0.0D), 10, 0.3D, 0.3D, 0.3D, 0.05D);
    }

    private void handleStrayHit(Entity damager, Player player) {
        boolean fromStray = damager instanceof Stray;
        if (damager instanceof Projectile projectile && projectile.getShooter() instanceof Stray) {
            fromStray = true;
        }
        if (!fromStray) {
            return;
        }
        startStrayFreeze(player);
    }

    private void startStrayFreeze(Player player) {
        UUID uuid = player.getUniqueId();
        restoreStrayHealth(player);
        ScheduledTask previous = strayFreezeTasks.remove(uuid);
        if (previous != null) {
            previous.cancel();
        }

        AttributeInstance maxHealth = player.getAttribute(Attribute.MAX_HEALTH);
        if (maxHealth == null) {
            return;
        }
        double originalMax = maxHealth.getBaseValue();
        strayOriginalMaxHealth.put(uuid, originalMax);

        int duration = Math.max(1, plugin.getConfig().getInt("harder-stray.freeze-duration-ticks", 100));
        int interval = Math.max(1, plugin.getConfig().getInt("harder-stray.freeze-damage-interval-ticks", 20));
        boolean reduceMaxHealth = plugin.getConfig().getBoolean("harder-stray.use-max-health-reduction", true);
        double reduction = Math.max(0.0D, plugin.getConfig().getDouble("harder-stray.max-health-reduction-per-interval", 0.5D));
        double directDamage = Math.max(0.0D, plugin.getConfig().getDouble("harder-stray.direct-damage-per-interval", 16.0D));
        int[] elapsed = {0};
        double[] currentMax = {originalMax};

        ScheduledTask task = player.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
            if (!player.isOnline() || player.isDead() || elapsed[0] >= duration || !plugin.enabled("harder-stray")) {
                restoreStrayHealth(player);
                strayFreezeTasks.remove(uuid);
                scheduledTask.cancel();
                return;
            }

            player.setFreezeTicks(Math.max(player.getFreezeTicks(), 140));
            int slowAmplifier = Math.max(0, plugin.getConfig().getInt("harder-stray.slowness-amplifier", 1));
            player.addPotionEffect(new PotionEffect(PotionEffectType.SLOWNESS, interval + 5, slowAmplifier, true, true, true));
            spawnFreezeParticles(player.getLocation().add(0.0D, 1.0D, 0.0D));

            if (reduceMaxHealth && reduction > 0.0D) {
                currentMax[0] = Math.max(1.0D, currentMax[0] - reduction);
                maxHealth.setBaseValue(currentMax[0]);
                if (player.getHealth() > currentMax[0]) {
                    player.setHealth(currentMax[0]);
                }
            } else if (directDamage > 0.0D) {
                player.damage(directDamage);
            }
            elapsed[0] += interval;
        }, () -> {
            restoreStrayHealth(player);
            strayFreezeTasks.remove(uuid);
        }, 1L, interval);
        if (task != null) {
            strayFreezeTasks.put(uuid, task);
        } else {
            restoreStrayHealth(player);
        }
    }

    private void restoreStrayHealth(Player player) {
        Double original = strayOriginalMaxHealth.remove(player.getUniqueId());
        if (original == null || !player.isValid()) {
            return;
        }
        AttributeInstance maxHealth = player.getAttribute(Attribute.MAX_HEALTH);
        if (maxHealth != null) {
            maxHealth.setBaseValue(original);
            if (player.getHealth() > maxHealth.getValue()) {
                player.setHealth(maxHealth.getValue());
            }
        }
    }

    private void spawnFreezeParticles(Location location) {
        int density = Math.max(1, plugin.getConfig().getInt("harder-stray.particle-density", 10));
        World world = location.getWorld();
        if (world != null) {
            world.spawnParticle(Particle.SNOWFLAKE, location, density, 0.4D, 0.6D, 0.4D, 0.02D);
            world.spawnParticle(Particle.ITEM_SNOWBALL, location, Math.max(1, density / 2), 0.3D, 0.4D, 0.3D, 0.01D);
        }
    }

    private void startArmadilloSphere(Armadillo armadillo, double incomingDamage) {
        UUID uuid = armadillo.getUniqueId();
        ScheduledTask old = armadilloSphereTasks.remove(uuid);
        if (old != null) {
            old.cancel();
        }

        AttributeInstance maxHealthAttribute = armadillo.getAttribute(Attribute.MAX_HEALTH);
        double maxHealth = maxHealthAttribute == null ? Math.max(1.0D, armadillo.getHealth()) : maxHealthAttribute.getValue();
        double healthAfter = Math.max(0.0D, armadillo.getHealth() - incomingDamage);
        double lostFraction = 1.0D - (healthAfter / Math.max(1.0D, maxHealth));
        double baseRadius = Math.max(0.5D, plugin.getConfig().getDouble("harder-armadillo.base-sphere-radius", 10.0D));
        double perStep = Math.max(0.0D, plugin.getConfig().getDouble("harder-armadillo.radius-per-20-percent-health-loss", 5.0D));
        double radius = baseRadius + Math.floor(lostFraction / 0.20D) * perStep;
        int duration = Math.max(1, plugin.getConfig().getInt("harder-armadillo.sphere-duration-ticks", 100));
        int period = Math.max(1, plugin.getConfig().getInt("harder-armadillo.check-period-ticks", 5));
        double pushForce = Math.max(0.0D, plugin.getConfig().getDouble("harder-armadillo.push-force", 1.5D));
        Set<UUID> playersInside = ConcurrentHashMap.newKeySet();
        int[] elapsed = {0};

        ScheduledTask task = armadillo.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
            if (!armadillo.isValid() || armadillo.isDead() || elapsed[0] >= duration || !plugin.enabled("harder-armadillo")) {
                armadilloSphereTasks.remove(uuid);
                playersInside.clear();
                scheduledTask.cancel();
                return;
            }

            Location center = armadillo.getLocation().clone();
            World world = armadillo.getWorld();
            drawSphere(center.clone().add(0.0D, 0.5D, 0.0D), radius);
            for (Player player : Bukkit.getOnlinePlayers()) {
                player.getScheduler().run(plugin, playerTask -> {
                    UUID playerId = player.getUniqueId();
                    if (!player.isOnline() || !player.getWorld().equals(world)) {
                        playersInside.remove(playerId);
                        return;
                    }
                    double distanceSquared = player.getLocation().distanceSquared(center);
                    if (distanceSquared > radius * radius) {
                        playersInside.remove(playerId);
                        return;
                    }
                    if (!playersInside.add(playerId)) {
                        return;
                    }
                    Vector push = player.getLocation().toVector().subtract(center.toVector());
                    if (push.lengthSquared() < 0.0001D) {
                        push = new Vector(1.0D, 0.2D, 0.0D);
                    }
                    push.normalize().multiply(pushForce);
                    push.setY(Math.max(0.25D, push.getY()));
                    player.setVelocity(player.getVelocity().add(push));
                }, null);
            }
            elapsed[0] += period;
        }, () -> {
            playersInside.clear();
            armadilloSphereTasks.remove(uuid);
        }, 1L, period);
        if (task != null) {
            armadilloSphereTasks.put(uuid, task);
        }
    }

    private void drawSphere(Location center, double radius) {
        World world = center.getWorld();
        if (world == null) {
            return;
        }
        int density = Math.max(8, plugin.getConfig().getInt("harder-armadillo.particle-density", 20));
        for (int index = 0; index < density; index++) {
            double theta = ThreadLocalRandom.current().nextDouble(0.0D, Math.PI * 2.0D);
            double phi = Math.acos(ThreadLocalRandom.current().nextDouble(-1.0D, 1.0D));
            double x = radius * Math.sin(phi) * Math.cos(theta);
            double y = radius * Math.cos(phi);
            double z = radius * Math.sin(phi) * Math.sin(theta);
            world.spawnParticle(Particle.WAX_ON, center.clone().add(x, y, z), 1, 0.0D, 0.0D, 0.0D, 0.0D);
        }
    }

    private void maybeMakeTridentZombie(Zombie zombie, CreatureSpawnEvent.SpawnReason reason) {
        if (zombie instanceof Drowned || zombie.getType() != EntityType.ZOMBIE) {
            return;
        }
        if (!isNaturalLike(reason)) {
            return;
        }
        double chance = normalizeChance(plugin.getConfig().getDouble("trident-zombie.spawn-chance", 0.03D));
        if (ThreadLocalRandom.current().nextDouble() >= chance) {
            return;
        }

        EntityEquipment equipment = zombie.getEquipment();
        if (equipment != null) {
            equipment.setItemInMainHand(new ItemStack(Material.TRIDENT));
            equipment.setItemInMainHandDropChance(0.0F);
        }
        zombie.getPersistentDataContainer().set(tridentZombieKey, PersistentDataType.BYTE, (byte) 1);

        multiplyBaseAttribute(zombie, Attribute.MAX_HEALTH, plugin.getConfig().getDouble("trident-zombie.health-multiplier", 1.5D));
        multiplyBaseAttribute(zombie, Attribute.ATTACK_DAMAGE, plugin.getConfig().getDouble("trident-zombie.damage-multiplier", 1.35D));
        multiplyBaseAttribute(zombie, Attribute.MOVEMENT_SPEED, plugin.getConfig().getDouble("trident-zombie.speed-multiplier", 1.10D));
        AttributeInstance maxHealth = zombie.getAttribute(Attribute.MAX_HEALTH);
        if (maxHealth != null) {
            zombie.setHealth(maxHealth.getValue());
        }
        startTridentZombieAttackTask(zombie);
    }

    private void startTridentZombieAttackTask(Zombie zombie) {
        UUID uuid = zombie.getUniqueId();
        if (tridentZombieTasks.containsKey(uuid)) {
            return;
        }
        long cooldown = Math.max(1L, plugin.getConfig().getLong("trident-zombie.attack-cooldown-ticks", 50L));
        ScheduledTask task = zombie.getScheduler().runAtFixedRate(plugin, scheduledTask -> {
            if (!zombie.isValid() || zombie.isDead() || !plugin.enabled("trident-zombie")) {
                tridentZombieTasks.remove(uuid);
                scheduledTask.cancel();
                return;
            }
            LivingEntity target = zombie.getTarget();
            if (target == null) {
                return;
            }
            Location origin = zombie.getEyeLocation().clone();
            double range = Math.max(1.0D, plugin.getConfig().getDouble("trident-zombie.attack-range", 18.0D));
            boolean lineOfSight = plugin.getConfig().getBoolean("trident-zombie.require-line-of-sight", true);

            target.getScheduler().run(plugin, targetTask -> {
                if (!target.isValid() || target.isDead() || !target.getWorld().equals(origin.getWorld())) {
                    return;
                }
                Location destination = target.getEyeLocation().clone();
                if (destination.distanceSquared(origin) > range * range) {
                    return;
                }
                Location targetSnapshot = destination.clone();
                zombie.getScheduler().run(plugin, zombieTask -> {
                    if (!zombie.isValid() || zombie.isDead() || !zombie.getWorld().equals(targetSnapshot.getWorld())) {
                        return;
                    }
                    if (lineOfSight && !zombie.hasLineOfSight(target)) {
                        return;
                    }
                    Vector direction = targetSnapshot.toVector().subtract(zombie.getEyeLocation().toVector());
                    if (direction.lengthSquared() < 0.0001D) {
                        return;
                    }
                    double speed = Math.max(0.1D, plugin.getConfig().getDouble("trident-zombie.trident-speed", 1.35D));
                    Trident trident = zombie.launchProjectile(Trident.class, direction.normalize().multiply(speed));
                    trident.setShooter(zombie);
                    trident.setPickupStatus(AbstractArrow.PickupStatus.DISALLOWED);
                    trident.getPersistentDataContainer().set(tridentZombieProjectileKey, PersistentDataType.BYTE, (byte) 1);
                    zombie.getWorld().playSound(zombie.getLocation(), Sound.ITEM_TRIDENT_THROW, 1.0F, 0.9F);
                }, null);
            }, null);
        }, () -> tridentZombieTasks.remove(uuid), 20L, cooldown);
        if (task != null) {
            tridentZombieTasks.put(uuid, task);
        }
    }

    private boolean isNaturalLike(CreatureSpawnEvent.SpawnReason reason) {
        return reason == CreatureSpawnEvent.SpawnReason.NATURAL
                || reason == CreatureSpawnEvent.SpawnReason.REINFORCEMENTS
                || reason == CreatureSpawnEvent.SpawnReason.PATROL
                || reason == CreatureSpawnEvent.SpawnReason.VILLAGE_INVASION;
    }

    private void multiplyBaseAttribute(LivingEntity entity, Attribute attribute, double multiplier) {
        AttributeInstance instance = entity.getAttribute(attribute);
        if (instance != null && multiplier > 0.0D) {
            instance.setBaseValue(instance.getBaseValue() * multiplier);
        }
    }

    private void addConfiguredLoot(EntityDeathEvent event) {
        String key = lootKey(event.getEntity());
        if (key == null) {
            return;
        }
        ConfigurationSection section = plugin.getConfig().getConfigurationSection("extra-loot." + key);
        if (section == null) {
            lootDebug("skipped mob=" + key + " entity=" + event.getEntity().getUniqueId()
                    + " reason=missing-mob-section");
            return;
        }
        if (!section.getBoolean("enabled", false)) {
            lootDebug("skipped mob=" + key + " entity=" + event.getEntity().getUniqueId()
                    + " reason=mob-loot-disabled");
            return;
        }
        LootAttribution attribution = resolveLootAttribution(event.getEntity());
        if (section.getBoolean("killed-by-player-only", true) && attribution == null) {
            lootDebug("skipped mob=" + key + " entity=" + event.getEntity().getUniqueId()
                    + " reason=no-player-attribution");
            return;
        }

        ConfigurationSection drops = section.getConfigurationSection("drops");
        if (drops == null) {
            lootDebug("skipped mob=" + key + " entity=" + event.getEntity().getUniqueId()
                    + " reason=missing-drops-section");
            return;
        }
        int looting = attribution == null ? 0 : attribution.lootingLevel();
        String killer = attribution == null ? "none" : attribution.playerName() + "/" + attribution.playerUuid();
        lootDebug("processing mob=" + key + " entity=" + event.getEntity().getUniqueId()
                + " killer=" + killer + " looting=" + looting + " configuredDrops=" + drops.getKeys(false).size());

        for (String dropKey : drops.getKeys(false)) {
            ConfigurationSection drop = drops.getConfigurationSection(dropKey);
            if (drop == null) {
                lootDebug("skipped mob=" + key + " drop=" + dropKey + " reason=invalid-drop-section");
                continue;
            }
            if (!drop.getBoolean("enabled", true)) {
                lootDebug("skipped mob=" + key + " drop=" + dropKey + " reason=drop-disabled");
                continue;
            }
            Material material = Material.matchMaterial(drop.getString("material", "AIR"));
            if (material == null || material.isAir()) {
                plugin.getLogger().warning("Invalid extra-loot material at extra-loot." + key + ".drops." + dropKey);
                continue;
            }
            double configuredChance = drop.getDouble("chance", 0.0D);
            double chance = normalizeChance(configuredChance);
            chance += normalizeChance(drop.getDouble("looting-bonus-per-level", 0.0D)) * looting;
            chance = Math.min(1.0D, chance);

            double roll = ThreadLocalRandom.current().nextDouble();
            if (roll >= chance) {
                lootDebug("roll mob=" + key + " drop=" + dropKey + " material=" + material
                        + " configuredChance=" + configuredChance + " effectiveChance=" + chance
                        + " roll=" + roll + " result=MISS");
                continue;
            }
            int min = Math.max(1, drop.getInt("min-amount", 1));
            int max = Math.max(min, drop.getInt("max-amount", min));
            int amount = ThreadLocalRandom.current().nextInt(min, max + 1);
            addDropStacks(event, material, amount);
            lootDebug("roll mob=" + key + " drop=" + dropKey + " material=" + material
                    + " configuredChance=" + configuredChance + " effectiveChance=" + chance
                    + " roll=" + roll + " amount=" + amount + " result=DROP");
        }
    }

    private void addDropStacks(EntityDeathEvent event, Material material, int totalAmount) {
        int maxStack = Math.max(1, material.getMaxStackSize());
        int remaining = totalAmount;
        while (remaining > 0) {
            int amount = Math.min(maxStack, remaining);
            event.getDrops().add(new ItemStack(material, amount));
            remaining -= amount;
        }
    }

    private String lootKey(LivingEntity entity) {
        if (entity.getPersistentDataContainer().has(tridentZombieKey, PersistentDataType.BYTE)) {
            return "trident-zombie";
        }
        if (entity instanceof Breeze) return "breeze";
        if (entity.getType() == EntityType.CREAKING) return "creaking";
        if (entity instanceof Bogged) return "bogged";
        if (entity instanceof Stray) return "stray";
        if (entity instanceof Armadillo) return "armadillo";
        if (entity instanceof Illusioner) return "illusioner";
        if (entity instanceof WitherSkeleton) return "wither-skeleton";
        if (entity instanceof Drowned) return "drowned";
        if (entity instanceof Pillager) return "pillager";
        if (entity instanceof Snowman) return "snow-golem";
        if (entity.getType() == EntityType.HAPPY_GHAST) return "happy-ghast";
        if (entity.getType() == EntityType.BLAZE) return "blaze";
        if (entity instanceof AbstractSkeleton) return "skeleton-sniper";
        return null;
    }

    private void cleanupEntity(LivingEntity entity) {
        UUID uuid = entity.getUniqueId();
        breezeTasks.remove(uuid);
        creakingTasks.remove(uuid);
        cancel(projectileRainTasks.remove(uuid));
        cancel(bleedingTasks.remove(uuid));
        cancel(buffRestoreTasks.remove(uuid));
        cancel(strayFreezeTasks.remove(uuid));
        cancel(armadilloSphereTasks.remove(uuid));
        cancel(tridentZombieTasks.remove(uuid));
        strayOriginalMaxHealth.remove(uuid);
        lastPlayerDamage.remove(uuid);
    }

    private void rememberLootAttribution(EntityDamageByEntityEvent event, LivingEntity target) {
        if (lootKey(target) == null) {
            return;
        }
        LootAttribution attribution = resolveDamageAttribution(event.getDamager());
        if (attribution == null) {
            return;
        }
        long now = System.currentTimeMillis();
        lastPlayerDamage.put(target.getUniqueId(), new LootAttribution(
                attribution.playerUuid(),
                attribution.playerName(),
                attribution.lootingLevel(),
                now
        ));
        if (lastPlayerDamage.size() > 4096) {
            lastPlayerDamage.entrySet().removeIf(entry ->
                    now - entry.getValue().recordedAtMillis() > LOOT_ATTRIBUTION_TTL_MILLIS);
        }
    }

    private LootAttribution resolveDamageAttribution(Entity damager) {
        long now = System.currentTimeMillis();
        if (damager instanceof Player player) {
            int looting = player.getInventory().getItemInMainHand().getEnchantmentLevel(Enchantment.LOOTING);
            return new LootAttribution(
                    player.getUniqueId(),
                    player.getName(),
                    Math.max(0, looting),
                    now
            );
        }
        if (!(damager instanceof Projectile projectile)) {
            return null;
        }

        String uuidValue = projectile.getPersistentDataContainer().get(lootShooterUuidKey, PersistentDataType.STRING);
        String name = projectile.getPersistentDataContainer().get(lootShooterNameKey, PersistentDataType.STRING);
        Integer looting = projectile.getPersistentDataContainer().get(lootShooterLootingKey, PersistentDataType.INTEGER);
        if (uuidValue != null && name != null) {
            try {
                return new LootAttribution(
                        UUID.fromString(uuidValue),
                        name,
                        Math.max(0, looting == null ? 0 : looting),
                        now
                );
            } catch (IllegalArgumentException invalidUuid) {
                lootDebug("ignored projectile attribution projectile=" + projectile.getUniqueId()
                        + " reason=invalid-player-uuid");
            }
        }

        if (projectile.getShooter() instanceof Player player) {
            return new LootAttribution(player.getUniqueId(), player.getName(), 0, now);
        }
        return null;
    }

    private LootAttribution resolveLootAttribution(LivingEntity entity) {
        LootAttribution cached = lastPlayerDamage.get(entity.getUniqueId());
        if (cached != null
                && System.currentTimeMillis() - cached.recordedAtMillis() <= LOOT_ATTRIBUTION_TTL_MILLIS) {
            return cached;
        }
        if (cached != null) {
            lastPlayerDamage.remove(entity.getUniqueId(), cached);
        }

        Player directKiller = entity.getKiller();
        if (directKiller != null) {
            return new LootAttribution(
                    directKiller.getUniqueId(),
                    directKiller.getName(),
                    0,
                    System.currentTimeMillis()
            );
        }

        return null;
    }

    private void lootDebug(String message) {
        if (plugin.getConfig().getBoolean("messages.debug", false)) {
            plugin.getLogger().info("[loot] " + message);
        }
    }

    private record LootAttribution(UUID playerUuid, String playerName, int lootingLevel, long recordedAtMillis) {
    }

    private double normalizeChance(double value) {
        if (value > 1.0D) {
            return Math.max(0.0D, Math.min(1.0D, value / 100.0D));
        }
        return Math.max(0.0D, Math.min(1.0D, value));
    }

    private void cancelAll(Map<UUID, ScheduledTask> tasks) {
        for (ScheduledTask task : new ArrayList<>(tasks.values())) {
            cancel(task);
        }
        tasks.clear();
    }

    private void cancel(ScheduledTask task) {
        if (task != null && !task.isCancelled()) {
            task.cancel();
        }
    }
}
