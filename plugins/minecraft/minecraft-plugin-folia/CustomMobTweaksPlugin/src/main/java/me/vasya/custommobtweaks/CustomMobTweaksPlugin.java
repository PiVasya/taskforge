package me.vasya.custommobtweaks;

import org.bukkit.Bukkit;
import org.bukkit.command.CommandSender;
import org.bukkit.command.PluginCommand;
import org.bukkit.configuration.InvalidConfigurationException;
import org.bukkit.configuration.file.YamlConfiguration;
import org.bukkit.entity.Player;
import org.bukkit.plugin.java.JavaPlugin;

import java.io.File;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.logging.Level;

public final class CustomMobTweaksPlugin extends JavaPlugin {

    private final List<PluginComponent> components = new ArrayList<>();
    private final Object lifecycleLock = new Object();
    private final AtomicBoolean reloadInProgress = new AtomicBoolean(false);
    private volatile HappyGhastBomberListener bomberListener;
    private volatile boolean runtimeStarted;

    @Override
    public void onEnable() {
        saveDefaultConfig();
        reloadConfig();

        PluginCommand command = getCommand("custommobtweaks");
        if (command != null) {
            MobTweaksCommand executor = new MobTweaksCommand(this);
            command.setExecutor(executor);
            command.setTabCompleter(executor);
        } else {
            getLogger().severe("Command custommobtweaks is missing from plugin.yml");
        }

        synchronized (lifecycleLock) {
            startRuntime("plugin-enable");
        }
        getLogger().info("CustomMobTweaks " + getDescription().getVersion() + " enabled for Folia 26.1.2");
    }

    @Override
    public void onDisable() {
        synchronized (lifecycleLock) {
            stopRuntime("plugin-disable");
        }
        getLogger().info("CustomMobTweaks disabled");
    }

    private void validateConfigFileBeforeReload() throws IOException, InvalidConfigurationException {
        File file = new File(getDataFolder(), "config.yml");
        YamlConfiguration candidate = new YamlConfiguration();
        candidate.load(file);
        getLogger().info("[reload] config preflight passed file=" + file.getAbsolutePath()
                + " keys=" + candidate.getKeys(true).size());
    }

    public void requestReload(CommandSender sender) {
        if (!reloadInProgress.compareAndSet(false, true)) {
            sendCommandMessage(sender, "§eCustomMobTweaks: перезагрузка уже выполняется.");
            return;
        }

        sendCommandMessage(sender, "§eCustomMobTweaks: перечитываю config.yml и перезапускаю все модули...");
        getLogger().info("[reload] requested by=" + sender.getName());

        Bukkit.getGlobalRegionScheduler().execute(this, () -> {
            try {
                validateConfigFileBeforeReload();
                synchronized (lifecycleLock) {
                    stopRuntime("command-reload");
                    reloadConfig();
                    startRuntime("command-reload");
                }
                sendCommandMessage(sender, "§aCustomMobTweaks: конфигурация и все модули применены без перезапуска сервера.");
            } catch (Throwable error) {
                getLogger().log(Level.SEVERE, "[reload] full runtime reload failed", error);
                sendCommandMessage(sender, "§cCustomMobTweaks: ошибка перезагрузки: "
                        + error.getClass().getSimpleName() + ": " + String.valueOf(error.getMessage()));
            } finally {
                reloadInProgress.set(false);
            }
        });
    }

    public boolean runtimeStarted() {
        return runtimeStarted;
    }

    public HappyGhastBomberListener bomberListener() {
        return bomberListener;
    }

    public boolean enabled(String path) {
        return getConfig().getBoolean(path + ".enabled", false);
    }

    private void startRuntime(String reason) {
        if (runtimeStarted || !components.isEmpty()) {
            throw new IllegalStateException("CustomMobTweaks runtime is already started");
        }

        getLogger().info("[reload] starting runtime reason=" + reason);
        try {
            RadiationManager radiationManager = new RadiationManager(this);
            HappyGhastBomberListener newBomberListener = new HappyGhastBomberListener(this, radiationManager);
            bomberListener = newBomberListener;

            registerComponent(new ListenerComponent(this, new MobEffectsListener(this)));
            registerComponent(new LegacyEnhancementsListener(this, radiationManager));
            registerComponent(new IllusionerSpawner(this));
            registerComponent(new FreezingSnowballListener(this));
            registerComponent(new LavaDamageListener(this));
            registerComponent(new DryWeaponListener(this));
            registerComponent(newBomberListener);
            registerComponent(radiationManager);

            runtimeStarted = true;
            logModuleState(reason);
            getLogger().info("[reload] runtime started reason=" + reason + " components=" + components.size());
        } catch (Throwable error) {
            getLogger().log(Level.SEVERE, "[reload] component startup failed; rolling back partial runtime", error);
            stopRuntime("startup-rollback");
            if (error instanceof RuntimeException runtimeException) throw runtimeException;
            if (error instanceof Error fatalError) throw fatalError;
            throw new IllegalStateException("CustomMobTweaks runtime startup failed", error);
        }
    }

    private void stopRuntime(String reason) {
        runtimeStarted = false;
        getLogger().info("[reload] stopping runtime reason=" + reason + " components=" + components.size());
        for (int index = components.size() - 1; index >= 0; index--) {
            PluginComponent component = components.get(index);
            try {
                component.shutdown();
                getLogger().info("[reload] stopped component=" + component.getClass().getSimpleName());
            } catch (Exception exception) {
                getLogger().log(Level.WARNING,
                        "[reload] could not stop component=" + component.getClass().getSimpleName(), exception);
            }
        }
        components.clear();
        bomberListener = null;
        getLogger().info("[reload] runtime stopped reason=" + reason);
    }

    private void logModuleState(String reason) {
        for (String module : List.of(
                "harder-creaking", "harder-breeze", "harder-bogged", "harder-armadillo",
                "harder-stray", "illusioner-spawn", "trident-zombie", "happy-ghast-bomber",
                "lava-damage", "dry-weapon", "freezing-snowball")) {
            getLogger().info("[reload] module=" + module + " enabled=" + enabled(module) + " reason=" + reason);
        }
    }

    private void sendCommandMessage(CommandSender sender, String message) {
        if (sender instanceof Player player) {
            player.getScheduler().run(this, task -> {
                if (player.isOnline()) player.sendMessage(message);
            }, () -> getLogger().info("[reload] command sender retired before response uuid="
                    + player.getUniqueId()));
            return;
        }
        sender.sendMessage(message);
    }

    private void registerComponent(PluginComponent component) {
        components.add(component);
        component.start();
        getLogger().info("[reload] started component=" + component.getClass().getSimpleName());
    }
}
