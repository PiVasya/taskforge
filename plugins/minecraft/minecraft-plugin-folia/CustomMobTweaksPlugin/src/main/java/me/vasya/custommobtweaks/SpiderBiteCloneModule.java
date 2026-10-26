package me.vasya.custommobtweaks;

import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import me.vasya.custommobtweaks.combat.SpiderCloneRules;
import org.bukkit.Bukkit;
import org.bukkit.NamespacedKey;
import org.bukkit.attribute.Attribute;
import org.bukkit.attribute.AttributeInstance;
import org.bukkit.entity.CaveSpider;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Spider;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.event.entity.EntityDamageEvent;
import org.bukkit.event.entity.EntityDeathEvent;
import org.bukkit.event.entity.EntityRemoveEvent;
import org.bukkit.persistence.PersistentDataType;

import java.util.ArrayList;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.logging.Level;

/** A real spider's successful melee bite creates one tiny, temporary spider of the same type. */
public final class SpiderBiteCloneModule implements PluginComponent, Listener {
    private final CustomMobTweaksPlugin plugin;
    private final NamespacedKey cloneKey;
    private final Map<UUID, Spider> activeClones = new ConcurrentHashMap<>();
    private volatile boolean running;

    public SpiderBiteCloneModule(CustomMobTweaksPlugin plugin) {
        this.plugin = plugin;
        cloneKey = new NamespacedKey(plugin, "spider_bite_clone");
    }

    @Override
    public void start() {
        running = plugin.enabled("spider-bite-clones");
        if (!running) return;
        Bukkit.getPluginManager().registerEvents(this, plugin);
    }

    @Override
    public void shutdown() {
        running = false;
        HandlerList.unregisterAll(this);
        // A reload may happen on the global region: never remove a mob from a foreign region.
        for (Spider clone : new ArrayList<>(activeClones.values())) {
            UUID id = clone.getUniqueId();
            clone.getScheduler().run(plugin, task -> {
                if (clone.isValid()) clone.remove();
                activeClones.remove(id);
            }, () -> activeClones.remove(id));
        }
        activeClones.clear();
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onBite(EntityDamageByEntityEvent event) {
        Spider original = event.getDamager() instanceof Spider spider ? spider : null;
        if (!SpiderCloneRules.shouldSpawn(running, original != null,
                original != null && isClone(original),
                event.getCause() == EntityDamageEvent.DamageCause.ENTITY_ATTACK,
                event.getEntity() instanceof LivingEntity, event.getFinalDamage())) {
            return;
        }
        spawnCopy(original, (LivingEntity) event.getEntity());
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onCloneDeath(EntityDeathEvent event) {
        if (event.getEntity() instanceof Spider spider && isClone(spider)) {
            event.getDrops().clear();
            event.setDroppedExp(0);
            activeClones.remove(spider.getUniqueId());
        }
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onCloneRemoved(EntityRemoveEvent event) {
        if (event.getEntity() instanceof Spider spider && isClone(spider)) {
            activeClones.remove(spider.getUniqueId());
        }
    }

    private void spawnCopy(Spider original, LivingEntity victim) {
        if (!running || !original.isValid() || original.isDead()) return;
        double size = SpiderCloneRules.scale(plugin.getConfig().getDouble("spider-bite-clones.scale", 0.35D));
        long lifetime = SpiderCloneRules.lifetimeTicks(
                plugin.getConfig().getLong("spider-bite-clones.lifetime-ticks", 100L));
        Spider clone = null;
        try {
            // The spawn consumer marks the entity before its spawn events can run.
            if (original instanceof CaveSpider) {
                clone = original.getWorld().spawn(original.getLocation(), CaveSpider.class,
                        created -> initializeCopy(created, size));
            } else {
                clone = original.getWorld().spawn(original.getLocation(), Spider.class,
                        created -> initializeCopy(created, size));
            }
            if (victim.isValid() && !victim.isDead()) {
                clone.setTarget(victim);
            }
            Spider spawned = clone;
            UUID id = clone.getUniqueId();
            activeClones.put(id, clone);
            ScheduledTask expiry = clone.getScheduler().runDelayed(plugin, task -> {
                try {
                    if (spawned.isValid()) spawned.remove();
                } finally {
                    activeClones.remove(id);
                }
            }, () -> activeClones.remove(id), lifetime);
            if (expiry == null) {
                activeClones.remove(id);
                if (clone.isValid()) clone.remove();
            }
        } catch (RuntimeException failure) {
            if (clone != null) {
                activeClones.remove(clone.getUniqueId());
                if (clone.isValid()) clone.remove();
            }
            plugin.getLogger().log(Level.WARNING,
                    "[spider-bite-clones] could not create temporary spider copy", failure);
        }
    }

    private void initializeCopy(Spider clone, double size) {
        clone.getPersistentDataContainer().set(cloneKey, PersistentDataType.BYTE, (byte) 1);
        AttributeInstance scale = clone.getAttribute(Attribute.SCALE);
        if (scale == null) throw new IllegalStateException("Spider scale attribute is unavailable");
        scale.setBaseValue(size);
        clone.setPersistent(false);
        clone.setRemoveWhenFarAway(true);
        clone.setCanPickupItems(false);
        clone.clearLootTable();
        clone.setAware(true);
        clone.setAggressive(true);
    }

    private boolean isClone(Spider spider) {
        return spider.getPersistentDataContainer().has(cloneKey, PersistentDataType.BYTE);
    }
}
