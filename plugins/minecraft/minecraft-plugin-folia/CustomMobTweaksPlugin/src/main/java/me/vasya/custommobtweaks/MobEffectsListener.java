package me.vasya.custommobtweaks;

import org.bukkit.Location;
import org.bukkit.NamespacedKey;
import org.bukkit.Particle;
import org.bukkit.World;
import org.bukkit.attribute.Attribute;
import org.bukkit.attribute.AttributeInstance;
import org.bukkit.configuration.file.FileConfiguration;
import org.bukkit.entity.AbstractArrow;
import org.bukkit.entity.AbstractSkeleton;
import org.bukkit.entity.Blaze;
import org.bukkit.entity.Bogged;
import org.bukkit.entity.Breeze;
import org.bukkit.entity.BreezeWindCharge;
import org.bukkit.entity.Drowned;
import org.bukkit.entity.Entity;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Monster;
import org.bukkit.entity.Pillager;
import org.bukkit.entity.Player;
import org.bukkit.entity.SmallFireball;
import org.bukkit.entity.Snowball;
import org.bukkit.entity.Snowman;
import org.bukkit.entity.Stray;
import org.bukkit.entity.Trident;
import org.bukkit.entity.WitherSkeleton;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.CreatureSpawnEvent;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.event.entity.EntityDamageEvent;
import org.bukkit.event.entity.EntityShootBowEvent;
import org.bukkit.event.entity.EntityTargetLivingEntityEvent;
import org.bukkit.event.entity.ProjectileHitEvent;
import org.bukkit.event.entity.ProjectileLaunchEvent;
import org.bukkit.persistence.PersistentDataType;
import org.bukkit.potion.PotionEffect;
import org.bukkit.potion.PotionEffectType;
import org.bukkit.util.Vector;

public final class MobEffectsListener implements Listener {

    private final CustomMobTweaksPlugin plugin;
    private final NamespacedKey pillagerPierceArrowKey;
    private final NamespacedKey harderBreezeChargeKey;

    public MobEffectsListener(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
        this.pillagerPierceArrowKey = new NamespacedKey(plugin, "pillager_pierce_arrow");
        this.harderBreezeChargeKey = new NamespacedKey(plugin, "harder_breeze_charge");
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onDamage(EntityDamageByEntityEvent event) {
        if (!(event.getEntity() instanceof LivingEntity target)) {
            return;
        }

        handleBlazeFireball(event, target);
        handleBoggedArrow(event, target);
        handleWitherSkeletonHit(event.getDamager(), target);
        handleSnowGolemSnowball(event, target);
        handlePillagerArmorPierce(event, target);
        handleStrayChill(event, target);
        handleBreezeFireballHit(event, target);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onProjectileLaunch(ProjectileLaunchEvent event) {
        tagPillagerArmorPierceArrow(event.getEntity());
        handleBreezePrimaryFireball(event);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onProjectileHit(ProjectileHitEvent event) {
        Entity hit = event.getHitEntity();
        if (!(hit instanceof LivingEntity target)) {
            return;
        }

        handleBreezeWindCharge(event.getEntity(), target);
        handleDrownedTrident(event.getEntity(), target);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onShootBow(EntityShootBowEvent event) {
        if (!(event.getEntity() instanceof LivingEntity shooter)) {
            return;
        }
        if (!(event.getProjectile() instanceof AbstractArrow arrow)) {
            return;
        }

        tagPillagerArmorPierceArrow(arrow);
        handleSkeletonSniper(shooter, arrow);
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onSkeletonSpawn(CreatureSpawnEvent event) {
        if (event.getEntity() instanceof AbstractSkeleton skeleton) {
            applySkeletonSniperAttributes(skeleton);
        }
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onSkeletonTarget(EntityTargetLivingEntityEvent event) {
        if (!(event.getEntity() instanceof AbstractSkeleton skeleton)) {
            return;
        }
        applySkeletonSniperAttributes(skeleton);
    }

    private void handleBlazeFireball(EntityDamageByEntityEvent event, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("blaze-fireball-lightning.enabled")) {
            return;
        }

        if (!(event.getDamager() instanceof SmallFireball fireball)) {
            return;
        }
        if (!(fireball.getShooter() instanceof Blaze)) {
            return;
        }

        World world = target.getWorld();
        Location loc = target.getLocation();
        boolean effectOnly = cfg.getBoolean("blaze-fireball-lightning.effect-only", true);
        int fireTicks = cfg.getInt("blaze-fireball-lightning.fire-ticks", 0);

        if (effectOnly) {
            world.strikeLightningEffect(loc);
        } else {
            world.strikeLightning(loc);
        }

        if (fireTicks > 0) {
            target.setFireTicks(Math.max(target.getFireTicks(), fireTicks));
        }

        debug("Blaze fireball hit " + target.getType());
    }

    private void handleBoggedArrow(EntityDamageByEntityEvent event, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("bogged-arrow-wither.enabled")) {
            return;
        }

        if (!(event.getDamager() instanceof AbstractArrow arrow)) {
            return;
        }
        if (!(arrow.getShooter() instanceof Bogged)) {
            return;
        }

        int duration = cfg.getInt("bogged-arrow-wither.duration-ticks", 40);
        int amplifier = cfg.getInt("bogged-arrow-wither.amplifier", 0);
        applyEffect(target, PotionEffectType.WITHER, duration, amplifier);

        debug("Bogged arrow hit " + target.getType());
    }

    private void handleWitherSkeletonHit(Entity damager, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("wither-skeleton-stun.enabled")) {
            return;
        }
        if (!(damager instanceof WitherSkeleton)) {
            return;
        }

        int duration = cfg.getInt("wither-skeleton-stun.duration-ticks", 30);
        int slownessAmp = cfg.getInt("wither-skeleton-stun.slowness-amplifier", 10);
        boolean weakness = cfg.getBoolean("wither-skeleton-stun.apply-weakness", true);
        boolean darkness = cfg.getBoolean("wither-skeleton-stun.apply-darkness", true);

        applyEffect(target, PotionEffectType.SLOWNESS, duration, slownessAmp);
        if (weakness) {
            applyEffect(target, PotionEffectType.WEAKNESS, duration, 1);
        }
        if (darkness && target instanceof Player) {
            applyEffect(target, PotionEffectType.DARKNESS, duration, 0);
        }

        target.getWorld().spawnParticle(
                Particle.ASH,
                target.getLocation().add(0.0, 1.0, 0.0),
                12,
                0.3, 0.4, 0.3,
                0.01
        );

        debug("Wither skeleton stunned " + target.getType());
    }

    private void handleSnowGolemSnowball(EntityDamageByEntityEvent event, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("snow-golem-snowball.enabled")) {
            return;
        }

        if (!(event.getDamager() instanceof Snowball snowball)) {
            return;
        }
        if (!(snowball.getShooter() instanceof Snowman)) {
            return;
        }
        if (!(target instanceof Monster)) {
            return;
        }

        double bonusDamage = cfg.getDouble("snow-golem-snowball.damage", 0.5D);
        if (bonusDamage > 0.0D) {
            event.setDamage(event.getDamage() + bonusDamage);
        }

        double slowChance = cfg.getDouble("snow-golem-snowball.slowness-chance", 0.0D);
        if (slowChance > 0.0D && Math.random() <= slowChance) {
            int duration = cfg.getInt("snow-golem-snowball.slowness-duration-ticks", 20);
            int amplifier = cfg.getInt("snow-golem-snowball.slowness-amplifier", 0);
            applyEffect(target, PotionEffectType.SLOWNESS, duration, amplifier);
        }

        debug("Snow golem snowball damaged " + target.getType());
    }

    private void handlePillagerArmorPierce(EntityDamageByEntityEvent event, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("pillager-armor-pierce.enabled")) {
            return;
        }

        if (!(event.getDamager() instanceof AbstractArrow arrow)) {
            return;
        }
        if (!isPillagerPierceArrow(arrow)) {
            return;
        }

        if (cfg.getBoolean("pillager-armor-pierce.ignore-armor", true)
                && event.isApplicable(EntityDamageEvent.DamageModifier.ARMOR)) {
            event.setDamage(EntityDamageEvent.DamageModifier.ARMOR, 0.0D);
        }

        if (cfg.getBoolean("pillager-armor-pierce.ignore-protection-enchants", true)
                && event.isApplicable(EntityDamageEvent.DamageModifier.MAGIC)) {
            event.setDamage(EntityDamageEvent.DamageModifier.MAGIC, 0.0D);
        }

        double bonusDamage = cfg.getDouble("pillager-armor-pierce.bonus-damage", 0.6D);
        if (bonusDamage > 0.0D && event.isApplicable(EntityDamageEvent.DamageModifier.BASE)) {
            event.setDamage(
                    EntityDamageEvent.DamageModifier.BASE,
                    event.getDamage(EntityDamageEvent.DamageModifier.BASE) + bonusDamage
            );
        }

        debug("Pillager armor-pierce hit " + target.getType());
    }

    private void handleStrayChill(EntityDamageByEntityEvent event, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("stray-chill.enabled")) {
            return;
        }

        if (!(event.getDamager() instanceof AbstractArrow arrow)) {
            return;
        }
        if (!(arrow.getShooter() instanceof Stray)) {
            return;
        }

        int duration = cfg.getInt("stray-chill.duration-ticks", 60);
        int amplifier = cfg.getInt("stray-chill.amplifier", 0);
        applyEffect(target, PotionEffectType.SLOWNESS, duration, amplifier);

        debug("Stray chilled " + target.getType());
    }

    private void handleBreezeFireballHit(EntityDamageByEntityEvent event, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("breeze-fireball-primary.enabled")) {
            return;
        }

        if (!(event.getDamager() instanceof SmallFireball fireball)) {
            return;
        }
        if (!(fireball.getShooter() instanceof Breeze)) {
            return;
        }

        double bonusDamage = cfg.getDouble("breeze-fireball-primary.bonus-damage", 1.0D);
        if (bonusDamage > 0.0D) {
            event.setDamage(event.getDamage() + bonusDamage);
        }

        int fireTicks = cfg.getInt("breeze-fireball-primary.fire-ticks", 40);
        if (fireTicks > 0) {
            target.setFireTicks(Math.max(target.getFireTicks(), fireTicks));
        }

        debug("Breeze fireball hit " + target.getType());
    }

    private void handleBreezePrimaryFireball(ProjectileLaunchEvent event) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("breeze-fireball-primary.enabled")) {
            return;
        }

        if (!(event.getEntity() instanceof BreezeWindCharge windCharge)) {
            return;
        }
        if (windCharge.getPersistentDataContainer().has(harderBreezeChargeKey, PersistentDataType.BYTE)) {
            return;
        }
        if (!(windCharge.getShooter() instanceof Breeze breeze)) {
            return;
        }

        Vector originalVelocity = windCharge.getVelocity().clone();
        double speedMultiplier = cfg.getDouble("breeze-fireball-primary.projectile-speed-multiplier", 1.15D);
        boolean incendiary = cfg.getBoolean("breeze-fireball-primary.incendiary", false);

        event.setCancelled(true);

        SmallFireball fireball = breeze.launchProjectile(SmallFireball.class);
        fireball.setShooter(breeze);
        fireball.setVelocity(originalVelocity.multiply(speedMultiplier));
        fireball.setIsIncendiary(incendiary);
        fireball.setYield(0.0F);

        debug("Replaced breeze wind charge with fireball");
    }

    private void handleBreezeWindCharge(Entity projectile, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("breeze-wind-charge.enabled")) {
            return;
        }

        if (!(projectile instanceof BreezeWindCharge windCharge)) {
            return;
        }
        if (!(windCharge.getShooter() instanceof Breeze)) {
            return;
        }

        double damage = cfg.getDouble("breeze-wind-charge.bonus-damage", 1.0D);
        double knockback = cfg.getDouble("breeze-wind-charge.vertical-knockback", 0.35D);
        boolean particles = cfg.getBoolean("breeze-wind-charge.spawn-cloud-particles", true);

        if (damage > 0.0D) {
            target.damage(damage, windCharge);
        }

        Vector velocity = target.getVelocity().clone();
        velocity.setY(Math.max(velocity.getY(), knockback));
        target.setVelocity(velocity);

        if (particles) {
            target.getWorld().spawnParticle(
                    Particle.CLOUD,
                    target.getLocation().add(0.0, 1.0, 0.0),
                    20,
                    0.35, 0.45, 0.35,
                    0.02
            );
        }

        debug("Breeze wind charge hit " + target.getType());
    }

    private void handleDrownedTrident(Entity projectile, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("drowned-trident-glow.enabled")) {
            return;
        }

        if (!(projectile instanceof Trident trident)) {
            return;
        }
        if (!(trident.getShooter() instanceof Drowned)) {
            return;
        }

        int duration = cfg.getInt("drowned-trident-glow.duration-ticks", 80);
        int amplifier = cfg.getInt("drowned-trident-glow.amplifier", 0);
        applyEffect(target, PotionEffectType.GLOWING, duration, amplifier);

        debug("Drowned trident marked " + target.getType());
    }

    private void handleSkeletonSniper(LivingEntity shooter, AbstractArrow arrow) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("skeleton-sniper.enabled")) {
            return;
        }
        if (!(shooter instanceof AbstractSkeleton skeleton)) {
            return;
        }
        if (!isEligibleSniperSkeleton(skeleton, cfg)) {
            return;
        }

        applySkeletonSniperAttributes(skeleton);

        LivingEntity target = skeleton.getTarget();
        if (target == null || target.isDead() || !target.getWorld().equals(shooter.getWorld())) {
            return;
        }

        double distance = shooter.getLocation().distance(target.getLocation());
        double minimumDistance = cfg.getDouble("skeleton-sniper.minimum-distance", 9.0D);
        if (distance < minimumDistance) {
            return;
        }

        double speedMultiplier = cfg.getDouble("skeleton-sniper.speed-multiplier", 1.22D);
        double baseDamageBonus = cfg.getDouble("skeleton-sniper.base-damage-bonus", 0.0D);
        double farShotDamageBonus = cfg.getDouble("skeleton-sniper.far-shot-damage-bonus", 0.35D);
        double predictionFactor = cfg.getDouble("skeleton-sniper.prediction-factor", 0.7D);
        boolean requireLineOfSight = cfg.getBoolean("skeleton-sniper.require-line-of-sight", true);
        boolean disableGravity = cfg.getBoolean("skeleton-sniper.disable-arrow-gravity", true);
        boolean retreatWhenClose = cfg.getBoolean("skeleton-sniper.retreat-when-close", true);
        double preferredDistance = cfg.getDouble("skeleton-sniper.preferred-distance", 24.0D);
        double retreatStrength = cfg.getDouble("skeleton-sniper.retreat-strength", 0.28D);
        boolean critical = cfg.getBoolean("skeleton-sniper.critical", false);

        if (requireLineOfSight && !skeleton.hasLineOfSight(target)) {
            return;
        }

        double currentArrowSpeed = Math.max(0.1D, arrow.getVelocity().length());
        double boostedArrowSpeed = currentArrowSpeed * speedMultiplier;
        double travelTicks = distance / boostedArrowSpeed;

        Location targetAim = getAimLocation(target, cfg.getBoolean("skeleton-sniper.aim-at-body", true));
        Vector predictiveLead = target.getVelocity().clone().multiply(travelTicks * predictionFactor);
        targetAim.add(predictiveLead);

        Location arrowLocation = arrow.getLocation();
        Vector direction = targetAim.toVector().subtract(arrowLocation.toVector()).normalize();

        arrow.setVelocity(direction.multiply(boostedArrowSpeed));
        arrow.setDamage(arrow.getDamage() + baseDamageBonus + farShotDamageBonus);

        if (disableGravity) {
            arrow.setGravity(false);
        }

        if (critical) {
            arrow.setCritical(true);
        }

        if (retreatWhenClose && skeleton.hasLineOfSight(target) && distance < preferredDistance) {
            Vector retreat = skeleton.getLocation().toVector().subtract(target.getLocation().toVector());
            if (retreat.lengthSquared() > 0.0001D) {
                retreat.normalize().multiply(retreatStrength);
                retreat.setY(Math.max(skeleton.getVelocity().getY(), 0.05D));
                skeleton.setVelocity(skeleton.getVelocity().multiply(0.35D).add(retreat));
            }
        }

        scheduleArrowHoming(arrow, target);
        debug("Skeleton sniper upgraded arrow at distance " + distance);
    }

    private void applySkeletonSniperAttributes(AbstractSkeleton skeleton) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("skeleton-sniper.enabled")) {
            return;
        }
        if (!isEligibleSniperSkeleton(skeleton, cfg)) {
            return;
        }

        double followRange = cfg.getDouble("skeleton-sniper.follow-range", 56.0D);
        AttributeInstance followRangeAttribute = skeleton.getAttribute(Attribute.FOLLOW_RANGE);
        if (followRangeAttribute != null && followRange > 0.0D && followRangeAttribute.getBaseValue() < followRange) {
            followRangeAttribute.setBaseValue(followRange);
        }
    }

    private boolean isEligibleSniperSkeleton(AbstractSkeleton skeleton, FileConfiguration cfg) {
        boolean allowStrays = cfg.getBoolean("skeleton-sniper.include-strays", true);
        boolean allowBogged = cfg.getBoolean("skeleton-sniper.include-bogged", false);

        if (!allowStrays && skeleton instanceof Stray) {
            return false;
        }
        if (!allowBogged && skeleton instanceof Bogged) {
            return false;
        }
        return !(skeleton instanceof WitherSkeleton);
    }

    private void scheduleArrowHoming(AbstractArrow arrow, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("skeleton-sniper.continuous-homing", true)) {
            return;
        }

        int homingTicks = cfg.getInt("skeleton-sniper.homing-ticks", 18);
        if (homingTicks <= 0) {
            return;
        }

        double turnRate = clamp(cfg.getDouble("skeleton-sniper.homing-turn-rate", 0.35D), 0.05D, 0.95D);
        double maxTrackDistance = cfg.getDouble("skeleton-sniper.max-track-distance", 40.0D);
        double maxTrackDistanceSquared = maxTrackDistance * maxTrackDistance;
        boolean aimAtBody = cfg.getBoolean("skeleton-sniper.aim-at-body", true);
        double leadFactor = cfg.getDouble("skeleton-sniper.homing-lead-factor", 0.35D);
        int[] remainingTicks = {homingTicks};

        arrow.getScheduler().runAtFixedRate(plugin, task -> {
            if (!arrow.isValid() || arrow.isDead()) {
                task.cancel();
                return;
            }
            if (remainingTicks[0]-- <= 0) {
                task.cancel();
                return;
            }
            if (!target.isValid() || target.isDead()) {
                task.cancel();
                return;
            }
            if (!arrow.getWorld().equals(target.getWorld())) {
                task.cancel();
                return;
            }

            Location arrowLocation = arrow.getLocation();
            Location targetLocation = getAimLocation(target, aimAtBody);
            if (arrowLocation.distanceSquared(targetLocation) > maxTrackDistanceSquared) {
                task.cancel();
                return;
            }

            Vector desired = targetLocation.toVector()
                    .add(target.getVelocity().clone().multiply(leadFactor))
                    .subtract(arrowLocation.toVector());
            if (desired.lengthSquared() < 0.0001D) {
                task.cancel();
                return;
            }

            double speed = Math.max(0.25D, arrow.getVelocity().length());
            Vector desiredVelocity = desired.normalize().multiply(speed);
            Vector newVelocity = arrow.getVelocity().clone().multiply(1.0D - turnRate)
                    .add(desiredVelocity.multiply(turnRate));

            if (newVelocity.lengthSquared() < 0.0001D) {
                task.cancel();
                return;
            }

            arrow.setVelocity(newVelocity);
        }, null, 1L, 1L);
    }

    private Location getAimLocation(LivingEntity target, boolean aimAtBody) {
        if (!aimAtBody) {
            return target.getEyeLocation().clone();
        }

        Location body = target.getLocation().clone();
        body.add(0.0D, Math.min(1.1D, Math.max(0.5D, target.getHeight() * 0.45D)), 0.0D);
        return body;
    }

    private void tagPillagerArmorPierceArrow(Entity projectile) {
        if (!(projectile instanceof AbstractArrow arrow)) {
            return;
        }
        if (!(arrow.getShooter() instanceof Pillager)) {
            return;
        }

        arrow.getPersistentDataContainer().set(pillagerPierceArrowKey, PersistentDataType.BYTE, (byte) 1);
    }

    private boolean isPillagerPierceArrow(AbstractArrow arrow) {
        Byte value = arrow.getPersistentDataContainer().get(pillagerPierceArrowKey, PersistentDataType.BYTE);
        return value != null && value == (byte) 1;
    }

    private void applyEffect(LivingEntity target, PotionEffectType type, int duration, int amplifier) {
        if (duration <= 0) {
            return;
        }

        target.addPotionEffect(new PotionEffect(
                type,
                duration,
                amplifier,
                true,
                true,
                true
        ));
    }

    private double clamp(double value, double min, double max) {
        return Math.max(min, Math.min(max, value));
    }

    private void debug(String text) {
        if (plugin.getConfig().getBoolean("messages.debug", false)) {
            plugin.getLogger().info(text);
        }
    }
}
