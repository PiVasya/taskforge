package me.vasya.custommobtweaks;

import org.bukkit.plugin.java.JavaPlugin;

public final class CustomMobTweaksPlugin extends JavaPlugin {

    @Override
    public void onEnable() {
        saveDefaultConfig();
        getServer().getPluginManager().registerEvents(new MobEffectsListener(this), this);
        getLogger().info("CustomMobTweaks enabled");
    }

    @Override
    public void onDisable() {
        getLogger().info("CustomMobTweaks disabled");
    }
}
