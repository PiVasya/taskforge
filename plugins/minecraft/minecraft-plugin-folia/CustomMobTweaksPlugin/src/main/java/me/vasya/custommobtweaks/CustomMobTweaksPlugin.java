package me.vasya.custommobtweaks;

import org.bukkit.plugin.java.JavaPlugin;

public final class CustomMobTweaksPlugin extends JavaPlugin {

    private MobEffectsListener mobEffectsListener;

    @Override
    public void onEnable() {
        saveDefaultConfig();
        mobEffectsListener = new MobEffectsListener(this);
        getServer().getPluginManager().registerEvents(mobEffectsListener, this);
        getLogger().info("[CustomMobTweaks][DEBUG] enabled version=" + getDescription().getVersion()
                + " debug=" + getConfig().getBoolean("messages.debug", true));
        for (String key : getConfig().getKeys(false)) {
            if (!"messages".equals(key)) {
                getLogger().info("[CustomMobTweaks][DEBUG] feature=" + key
                        + " enabled=" + getConfig().getBoolean(key + ".enabled", false));
            }
        }
    }

    @Override
    public void onDisable() {
        getLogger().info("[CustomMobTweaks][DEBUG] disabled");
    }

    public void reloadPluginConfig() {
        reloadConfig();
    }
}
