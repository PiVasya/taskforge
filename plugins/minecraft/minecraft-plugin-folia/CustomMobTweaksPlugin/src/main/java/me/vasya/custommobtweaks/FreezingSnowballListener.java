package me.vasya.custommobtweaks;

import org.bukkit.Bukkit;
import org.bukkit.Particle;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Player;
import org.bukkit.entity.Snowball;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.potion.PotionEffect;
import org.bukkit.potion.PotionEffectType;

public final class FreezingSnowballListener implements Listener, PluginComponent {
    private final CustomMobTweaksPlugin plugin;

    public FreezingSnowballListener(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
    }

    @Override
    public void start() {
        Bukkit.getPluginManager().registerEvents(this, plugin);
    }

    @Override
    public void shutdown() {
        HandlerList.unregisterAll(this);
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void onSnowballHit(EntityDamageByEntityEvent event) {
        if (!plugin.enabled("freezing-snowball")
                || !(event.getDamager() instanceof Snowball)
                || !(event.getEntity() instanceof LivingEntity target)) {
            return;
        }
        if (plugin.getConfig().getBoolean("freezing-snowball.players-only", false) && !(target instanceof Player)) {
            return;
        }

        int duration = Math.max(1, plugin.getConfig().getInt("freezing-snowball.freeze-duration-ticks", 60));
        int slowness = Math.max(0, plugin.getConfig().getInt("freezing-snowball.slowness-amplifier", 2));
        int fatigue = Math.max(0, plugin.getConfig().getInt("freezing-snowball.mining-fatigue-amplifier", 1));
        int density = Math.max(1, plugin.getConfig().getInt("freezing-snowball.particle-density", 15));

        target.setFreezeTicks(Math.max(target.getFreezeTicks(), duration));
        target.addPotionEffect(new PotionEffect(PotionEffectType.SLOWNESS, duration, slowness, true, true, true));
        target.addPotionEffect(new PotionEffect(PotionEffectType.MINING_FATIGUE, duration, fatigue, true, true, true));
        target.getWorld().spawnParticle(Particle.SNOWFLAKE, target.getLocation().add(0.0D, 1.0D, 0.0D), density, 0.4D, 0.5D, 0.4D, 0.02D);
    }
}
