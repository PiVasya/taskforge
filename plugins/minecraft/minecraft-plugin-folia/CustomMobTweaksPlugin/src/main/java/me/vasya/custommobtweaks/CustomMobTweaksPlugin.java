package me.vasya.custommobtweaks;

import org.bukkit.command.PluginCommand;
import org.bukkit.plugin.java.JavaPlugin;

import java.util.ArrayList;
import java.util.List;

public final class CustomMobTweaksPlugin extends JavaPlugin {

    private final List<PluginComponent> components = new ArrayList<>();
    private HappyGhastBomberListener bomberListener;

    @Override
    public void onEnable() {
        saveDefaultConfig();

        RadiationManager radiationManager = new RadiationManager(this);
        bomberListener = new HappyGhastBomberListener(this, radiationManager);

        registerComponent(new ListenerComponent(this, new MobEffectsListener(this)));
        registerComponent(new LegacyEnhancementsListener(this, radiationManager));
        registerComponent(new IllusionerSpawner(this));
        registerComponent(new FreezingSnowballListener(this));
        registerComponent(new LavaDamageListener(this));
        registerComponent(new DryWeaponListener(this));
        registerComponent(bomberListener);
        registerComponent(radiationManager);

        PluginCommand command = getCommand("custommobtweaks");
        if (command != null) {
            MobTweaksCommand executor = new MobTweaksCommand(this, bomberListener);
            command.setExecutor(executor);
            command.setTabCompleter(executor);
        }

        getLogger().info("CustomMobTweaks 2.0.0 enabled for Folia 26.1.2");
    }

    @Override
    public void onDisable() {
        for (int index = components.size() - 1; index >= 0; index--) {
            try {
                components.get(index).shutdown();
            } catch (Exception exception) {
                getLogger().warning("Could not stop component: " + exception.getMessage());
            }
        }
        components.clear();
        getLogger().info("CustomMobTweaks disabled");
    }

    public void reloadPluginConfig() {
        reloadConfig();
        getLogger().info("CustomMobTweaks configuration reloaded");
    }

    public boolean enabled(String path) {
        return getConfig().getBoolean(path + ".enabled", false);
    }

    private void registerComponent(PluginComponent component) {
        components.add(component);
        component.start();
    }
}
