package me.vasya.custommobtweaks;

import me.vasya.custommobtweaks.dragon.EnderDragonRework;

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
    private volatile PhantomDiveCloneManager phantomDiveCloneManager;
    private volatile EnderDragonRework enderDragonRework;
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

    public String phantomDiveCloneDiagnostics() {
        PhantomDiveCloneManager manager = phantomDiveCloneManager;
        return manager == null ? "phantom-dive-clones=stopped" : manager.diagnosticsSummary();
    }

    public String dragonDiagnostics() {
        EnderDragonRework rework = enderDragonRework;
        return rework == null ? "dragon-runtime=stopped" : rework.diagnosticsSummary();
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
            HappyGhastBomberListener newBomberListener = new HappyGhastBomberListener(this);
            bomberListener = newBomberListener;

            registerComponent(new ListenerComponent(this, new MobEffectsListener(this)));
            registerComponent(new LegacyEnhancementsListener(this));
            registerComponent(new IllusionerSpawner(this));
            registerComponent(new IllusionerCloneManager(this));
            registerComponent(new FreezingSnowballListener(this));
            registerComponent(new LavaDamageListener(this));
            registerComponent(new DryWeaponListener(this));
            PhantomDiveCloneManager newPhantomDiveCloneManager = new PhantomDiveCloneManager(this);
            phantomDiveCloneManager = newPhantomDiveCloneManager;
            registerComponent(newPhantomDiveCloneManager);
            EnderDragonRework newEnderDragonRework = new EnderDragonRework(this);
            enderDragonRework = newEnderDragonRework;
            registerComponent(newEnderDragonRework);
            registerComponent(newBomberListener);

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
        phantomDiveCloneManager = null;
        enderDragonRework = null;
        getLogger().info("[reload] runtime stopped reason=" + reason);
    }

    private void logModuleState(String reason) {
        for (String module : List.of(
                "harder-creaking", "harder-breeze", "harder-bogged", "harder-armadillo",
                "harder-stray", "illusioner-spawn", "illusioner-clones", "trident-zombie", "happy-ghast-bomber",
                "lava-damage", "dry-weapon", "freezing-snowball", "phantom-dive-clones", "ender-dragon-rework")) {
            getLogger().info("[reload] module=" + module + " enabled=" + enabled(module) + " reason=" + reason);
        }
        getLogger().info("[reload] phantomDiveClones enabled=" + enabled("phantom-dive-clones")
                + " monitorPeriodTicks=" + getConfig().getLong("phantom-dive-clones.monitor-period-ticks", 1L)
                + " cooldownTicks=" + getConfig().getLong("phantom-dive-clones.cooldown-ticks", 40L)
                + " health=" + getConfig().getDouble("phantom-dive-clones.health", 1.0D)
                + " size=" + getConfig().getInt("phantom-dive-clones.size", 0)
                + " lifetimeTicks=" + getConfig().getLong("phantom-dive-clones.lifetime-ticks", 200L)
                + " maximumActiveTotal=" + getConfig().getInt("phantom-dive-clones.maximum-active-total", 96)
                + " recursiveCopies=false"
                + " reason=" + reason);
        getLogger().info("[reload] dragon enabled=" + enabled("ender-dragon-rework")
                + " debug=" + getConfig().getBoolean("ender-dragon-rework.debug", false)
                + " fireStream=" + getConfig().getBoolean("ender-dragon-rework.fire-stream.enabled", true)
                + " replaceStrafingFireball=" + getConfig().getBoolean("ender-dragon-rework.fire-stream.replace-strafing-fireball", true)
                + " replacePerchedBreath=" + getConfig().getBoolean("ender-dragon-rework.fire-stream.replace-perched-breath", true)
                + " durationTicks=" + getConfig().getLong("ender-dragon-rework.fire-stream.duration-ticks", 140L)
                + " length=" + getConfig().getDouble("ender-dragon-rework.fire-stream.length", 42.0D)
                + " maxRadius=" + getConfig().getDouble("ender-dragon-rework.fire-stream.maximum-radius", 5.5D)
                + " groundFire=" + getConfig().getBoolean("ender-dragon-rework.fire-stream.ground.place-fire", true)
                + " breathClouds=" + getConfig().getBoolean("ender-dragon-rework.fire-stream.ground.create-dragon-breath-clouds", true)
                + " phantomFlockMin=" + getConfig().getInt("ender-dragon-rework.phantoms.flock-size-min", 5)
                + " phantomFlockMax=" + getConfig().getInt("ender-dragon-rework.phantoms.flock-size-max", 6)
                + " phantomMaximumActive=" + getConfig().getInt("ender-dragon-rework.phantoms.maximum-active", 12)
                + " phantomSizeMin=" + getConfig().getInt("ender-dragon-rework.phantoms.size-min", 1)
                + " phantomSizeMax=" + getConfig().getInt("ender-dragon-rework.phantoms.size-max", 3)
                + " healthMultiplier=" + getConfig().getDouble("ender-dragon-rework.primary-dragon.health-multiplier", 2.0D)
                + " voidRoar=" + getConfig().getBoolean("ender-dragon-rework.void-roar.enabled", true)
                + " voidRoarRadius=" + getConfig().getDouble("ender-dragon-rework.void-roar.maximum-radius", 20.0D)
                + " crystalField=" + getConfig().getBoolean("ender-dragon-rework.crystal-field.enabled", true)
                + " crystalCountMin=" + getConfig().getInt("ender-dragon-rework.crystal-field.count-min", 18)
                + " crystalCountMax=" + getConfig().getInt("ender-dragon-rework.crystal-field.count-max", 24)
                + " crystalMaximumActive=" + getConfig().getInt("ender-dragon-rework.crystal-field.maximum-active", 48)
                + " dragonBreathCrystalProtection=true"
                + " purpleAura=true ordinaryPhantomAppearanceUntouched=true allPhantomsCloneOnDive=true"
                + " reason=" + reason);
        getLogger().info("[reload] hazard-zones=REMOVED existing-config-keys-ignored=true reason=" + reason);
        getLogger().info("[reload] breeze-elytra chance="
                + getConfig().getDouble("extra-loot.breeze.drops.elytra.chance", 0.05D)
                + " forced=false reason=" + reason);
        getLogger().info("[reload] illusioner-clones maxActive="
                + getConfig().getInt("illusioner-clones.max-active-per-original", 30)
                + " maxGeneration="
                + getConfig().getInt("illusioner-clones.max-generation", 7)
                + " lifetimeTicks="
                + getConfig().getLong("illusioner-clones.base-lifetime-ticks", 300L)
                + " minimumTicks="
                + getConfig().getLong("illusioner-clones.minimum-lifetime-ticks", 60L)
                + " verboseLogs="
                + getConfig().getBoolean("illusioner-clones.verbose-logs", true)
                + " appliesToAllIllusioners=true globalPolling=false reason=" + reason);
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
