package me.vasya.custommobtweaks;

import org.bukkit.plugin.java.JavaPlugin;

public final class CustomMobTweaksPlugin extends JavaPlugin {

    private MobEffectsListener mobEffectsListener;

    @Override
    public void onEnable() {
        saveDefaultConfig();
        mobEffectsListener = new MobEffectsListener(this);
        getServer().getPluginManager().registerEvents(mobEffectsListener, this);
        getLogger().info("CustomMobTweaks enabled");
    }

    @Override
    public void onDisable() {
        getLogger().info("CustomMobTweaks disabled");
    }

    public void reloadPluginConfig() {
        reloadConfig();
    }
}
