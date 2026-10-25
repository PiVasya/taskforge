package me.vasya.custommobtweaks.combat;

import com.destroystokyo.paper.entity.ai.Goal;
import com.destroystokyo.paper.entity.ai.GoalKey;
import com.destroystokyo.paper.entity.ai.GoalType;
import com.destroystokyo.paper.event.entity.EntityAddToWorldEvent;
import io.papermc.paper.threadedregions.scheduler.ScheduledTask;
import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import me.vasya.custommobtweaks.PluginComponent;
import org.bukkit.Bukkit;
import org.bukkit.NamespacedKey;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Mob;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.CreatureSpawnEvent;
import org.bukkit.event.entity.EntityDeathEvent;
import org.bukkit.event.world.EntitiesLoadEvent;
import org.bukkit.event.world.EntitiesUnloadEvent;

import java.util.ArrayList;
import java.util.EnumSet;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.BooleanSupplier;
import java.util.logging.Level;

/** Enrollment is event-driven; every controller and AI change belongs to its entity's region. */
public abstract class ManagedMobModule implements PluginComponent, Listener {
    protected final CustomMobTweaksPlugin plugin;
    protected final String module;
    protected final Map<UUID, Session> sessions = new ConcurrentHashMap<>();
    protected volatile boolean running;
    private final long period;

    protected ManagedMobModule(CustomMobTweaksPlugin plugin, String module, long period) {
        this.plugin = plugin;
        this.module = module;
        this.period = period;
    }

    protected abstract boolean accepts(Entity entity);
    protected abstract Session createSession(Mob entity);

    @Override
    public final void start() {
        running = plugin.enabled(module);
        if (!running) return;
        Bukkit.getPluginManager().registerEvents(this, plugin);
        // A single bootstrap pass also covers mobs that existed before /cmt reload.
        for (var world : Bukkit.getWorlds()) {
            for (var chunk : world.getLoadedChunks()) {
                int x = chunk.getX(), z = chunk.getZ();
                Bukkit.getRegionScheduler().execute(plugin, world, x, z, () -> {
                    if (!running || !world.isChunkLoaded(x, z)) return;
                    for (Entity entity : world.getChunkAt(x, z).getEntities()) enroll(entity);
                });
            }
        }
        debug("started controllerPeriodTicks=" + period);
    }

    @Override
    public final void shutdown() {
        running = false;
        HandlerList.unregisterAll(this);
        for (Session session : List.copyOf(sessions.values())) session.stop();
        sessions.clear();
        debug("stopped");
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public final void onSpawn(CreatureSpawnEvent event) { enroll(event.getEntity()); }

    @EventHandler(priority = EventPriority.MONITOR)
    public final void onAdded(EntityAddToWorldEvent event) { enroll(event.getEntity()); }

    @EventHandler(priority = EventPriority.MONITOR)
    public final void onLoad(EntitiesLoadEvent event) {
        for (Entity entity : event.getEntities()) enroll(entity);
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public final void onUnload(EntitiesUnloadEvent event) {
        for (Entity entity : event.getEntities()) {
            Session session = sessions.get(entity.getUniqueId());
            if (session != null) session.stop();
        }
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public final void onDeath(EntityDeathEvent event) {
        Session session = sessions.get(event.getEntity().getUniqueId());
        if (session != null) session.stop();
    }

    protected final void enroll(Entity entity) {
        if (!running || !(entity instanceof Mob mob) || !accepts(entity)) return;
        mob.getScheduler().run(plugin, task -> {
            if (!running || !mob.isValid() || mob.isDead() || sessions.containsKey(mob.getUniqueId())) return;
            Session session = createSession(mob);
            if (sessions.putIfAbsent(mob.getUniqueId(), session) == null) session.begin();
        }, null);
    }

    protected final double number(String key, double fallback, double min, double max) {
        return CombatMath.clamp(plugin.getConfig().getDouble(module + "." + key, fallback), min, max, fallback);
    }

    protected final int ticks(String key, int fallback, int min, int max) {
        return Math.max(min, Math.min(max, plugin.getConfig().getInt(module + "." + key, fallback)));
    }

    protected final boolean flag(String key, boolean fallback) {
        return plugin.getConfig().getBoolean(module + "." + key, fallback);
    }

    protected final void debug(String text) {
        if (plugin.getConfig().getBoolean(module + ".debug", plugin.getConfig().getBoolean("messages.debug", false))) {
            plugin.getLogger().info("[" + module + "] " + text);
        }
    }

    protected abstract class Session {
        protected final Mob mob;
        protected volatile boolean active = true;
        protected long age;
        private volatile ScheduledTask task;
        private final AtomicBoolean stopped = new AtomicBoolean();
        private final List<Goal<Mob>> guards = new ArrayList<>();

        protected Session(Mob mob) { this.mob = mob; }
        protected void initialize() { }
        protected abstract void tick();
        protected void cleanup() { }

        private void begin() {
            if (!running || !active) { stop(); return; }
            try {
                initialize();
                task = mob.getScheduler().runAtFixedRate(plugin, scheduled -> {
                    if (!running || !active || !mob.isValid() || mob.isDead()) {
                        scheduled.cancel();
                        stop();
                        return;
                    }
                    try {
                        age += period;
                        tick();
                    } catch (RuntimeException exception) {
                        plugin.getLogger().log(Level.SEVERE, "[" + module + "] controller failed uuid="
                                + mob.getUniqueId() + "; restoring vanilla AI", exception);
                        stop();
                    }
                }, () -> {
                    active = false;
                    sessions.remove(mob.getUniqueId(), this);
                }, 1L, period);
                if (task == null) stop();
                else if (!running || !active) { task.cancel(); stop(); }
            } catch (RuntimeException exception) {
                plugin.getLogger().log(Level.SEVERE, "[" + module + "] enrollment failed uuid="
                        + mob.getUniqueId(), exception);
                stop();
            }
        }

        /** Lock only the requested goal selector, without deleting any vanilla goals. */
        protected final void guard(String name, BooleanSupplier condition, GoalType... types) {
            Goal<Mob> goal = new Goal<>() {
                private final GoalKey<Mob> key = GoalKey.of(Mob.class,
                        new NamespacedKey(plugin, module.replace('-', '_') + "_" + name));
                @Override public boolean shouldActivate() { return running && active && condition.getAsBoolean(); }
                @Override public boolean shouldStayActive() { return shouldActivate(); }
                @Override public GoalKey<Mob> getKey() { return key; }
                @Override public EnumSet<GoalType> getTypes() {
                    EnumSet<GoalType> result = EnumSet.noneOf(GoalType.class);
                    for (GoalType type : types) result.add(type);
                    return result;
                }
            };
            guards.add(goal);
            Bukkit.getMobGoals().addGoal(mob, -1, goal);
        }

        protected final void stop() {
            if (!stopped.compareAndSet(false, true)) return;
            active = false;
            ScheduledTask scheduled = task;
            if (scheduled != null) scheduled.cancel();
            sessions.remove(mob.getUniqueId(), this);
            Runnable restore = () -> {
                for (Goal<Mob> guard : guards) Bukkit.getMobGoals().removeGoal(mob, guard);
                guards.clear();
                cleanup();
            };
            if (Bukkit.isOwnedByCurrentRegion(mob)) {
                restore.run();
            } else if (plugin.isEnabled()) {
                mob.getScheduler().run(plugin, ignored -> restore.run(), null);
            }
        }
    }
}
