package me.vasya.custommobtweaks;

import org.bukkit.Bukkit;
import org.bukkit.event.HandlerList;
import org.bukkit.event.Listener;

public final class ListenerComponent implements PluginComponent {
    private final CustomMobTweaksPlugin plugin;
    private final Listener listener;

    public ListenerComponent(CustomMobTweaksPlugin plugin, Listener listener) {
        this.plugin = plugin;
        this.listener = listener;
    }

    @Override
    public void start() {
        Bukkit.getPluginManager().registerEvents(listener, plugin);
    }

    @Override
    public void shutdown() {
        HandlerList.unregisterAll(listener);
        if (listener instanceof PluginComponent component) {
            component.shutdown();
        }
    }
}
