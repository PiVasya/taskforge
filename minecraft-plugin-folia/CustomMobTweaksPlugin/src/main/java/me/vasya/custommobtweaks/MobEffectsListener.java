package me.vasya.custommobtweaks;

import org.bukkit.Location;
import org.bukkit.Particle;
import org.bukkit.World;
import org.bukkit.configuration.file.FileConfiguration;
import org.bukkit.entity.AbstractArrow;
import org.bukkit.entity.Blaze;
import org.bukkit.entity.Bogged;
import org.bukkit.entity.Entity;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Player;
import org.bukkit.entity.SmallFireball;
import org.bukkit.entity.WitherSkeleton;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.potion.PotionEffect;
import org.bukkit.potion.PotionEffectType;

public final class MobEffectsListener implements Listener {

    private final CustomMobTweaksPlugin plugin;

    public MobEffectsListener(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onDamage(EntityDamageByEntityEvent event) {
        if (!(event.getEntity() instanceof LivingEntity target)) {
            return;
        }

        handleBlazeFireball(event.getDamager(), target);
        handleBoggedArrow(event.getDamager(), target);
        handleWitherSkeletonHit(event.getDamager(), target);
    }

    private void handleBlazeFireball(Entity damager, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("blaze-fireball-lightning.enabled")) {
            return;
        }

        if (!(damager instanceof SmallFireball fireball)) {
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

        debug("Blaze fireball hit " + target.getName());
    }

    private void handleBoggedArrow(Entity damager, LivingEntity target) {
        FileConfiguration cfg = plugin.getConfig();
        if (!cfg.getBoolean("bogged-arrow-wither.enabled")) {
            return;
        }

        if (!(damager instanceof AbstractArrow arrow)) {
            return;
        }

        if (!(arrow.getShooter() instanceof Bogged)) {
            return;
        }

        int duration = cfg.getInt("bogged-arrow-wither.duration-ticks", 40);
        int amplifier = cfg.getInt("bogged-arrow-wither.amplifier", 0);

        target.addPotionEffect(new PotionEffect(
                PotionEffectType.WITHER,
                duration,
                amplifier,
                true,
                true,
                true
        ));

        debug("Bogged arrow hit " + target.getName());
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

        target.addPotionEffect(new PotionEffect(
                PotionEffectType.SLOWNESS,
                duration,
                slownessAmp,
                true,
                true,
                true
        ));

        if (weakness) {
            target.addPotionEffect(new PotionEffect(
                    PotionEffectType.WEAKNESS,
                    duration,
                    1,
                    true,
                    true,
                    true
            ));
        }

        if (darkness && target instanceof Player) {
            target.addPotionEffect(new PotionEffect(
                    PotionEffectType.DARKNESS,
                    duration,
                    0,
                    true,
                    true,
                    true
            ));
        }

        target.getWorld().spawnParticle(
                Particle.ASH,
                target.getLocation().add(0, 1, 0),
                12,
                0.3, 0.4, 0.3,
                0.01
        );

        debug("Wither skeleton hit " + target.getName());
    }

    private void debug(String text) {
        if (plugin.getConfig().getBoolean("messages.debug", false)) {
            plugin.getLogger().info(text);
        }
    }
}
