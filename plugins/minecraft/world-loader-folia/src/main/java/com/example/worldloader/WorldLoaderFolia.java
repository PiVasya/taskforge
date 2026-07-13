package com.example.worldloader;

import org.bukkit.Bukkit;
import org.bukkit.ChatColor;
import org.bukkit.Location;
import org.bukkit.NamespacedKey;
import org.bukkit.World;
import org.bukkit.WorldCreator;
import org.bukkit.command.Command;
import org.bukkit.command.CommandExecutor;
import org.bukkit.command.CommandSender;
import org.bukkit.command.TabCompleter;
import org.bukkit.configuration.ConfigurationSection;
import org.bukkit.entity.Player;
import org.bukkit.plugin.java.JavaPlugin;

import java.io.File;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;
import java.util.Objects;
import java.util.function.Consumer;

/**
 * Small Folia-safe helper for already registered worlds.
 *
 * On TaskForge production the Worlds plugin and plugins/Worlds/worlds.dat are the
 * source of truth. Namespace worlds are therefore never created by this plugin.
 */
public final class WorldLoaderFolia extends JavaPlugin implements CommandExecutor, TabCompleter {

    @Override
    public void onEnable() {
        saveDefaultConfig();

        Objects.requireNonNull(getCommand("loadworld")).setExecutor(this);
        Objects.requireNonNull(getCommand("tpsurv")).setExecutor(this);
        Objects.requireNonNull(getCommand("tplobby")).setExecutor(this);
        Objects.requireNonNull(getCommand("loadworld")).setTabCompleter(this);

        if (getConfig().getBoolean("autoload-survival-on-start", false)) {
            String survival = getConfig().getString("survival-world", "owp:overworld");
            resolveOrLoadWorld(survival, world -> {
                if (world == null) {
                    getLogger().warning("Survival world is not loaded: " + survival
                        + ". Load it through Worlds/worlds.dat; no replacement world was created.");
                }
            });
        }

        getLogger().info("WorldLoaderFolia enabled; namespace world creation is blocked.");
    }

    private World findLoadedWorld(String rawName) {
        String worldName = rawName == null ? "" : rawName.trim();
        if (worldName.isEmpty()) return null;

        World byName = Bukkit.getWorld(worldName);
        if (byName != null) return byName;

        NamespacedKey key = NamespacedKey.fromString(worldName);
        return key == null ? null : Bukkit.getWorld(key);
    }

    private boolean isNamespaceWorld(String name) {
        return name != null && name.indexOf(':') >= 0;
    }

    private void resolveOrLoadWorld(String rawName, Consumer<World> callback) {
        String worldName = rawName == null ? "" : rawName.trim();
        if (worldName.isEmpty()) {
            callback.accept(null);
            return;
        }

        World loaded = findLoadedWorld(worldName);
        if (loaded != null) {
            callback.accept(loaded);
            return;
        }

        boolean worldsOwnsRegistry = Bukkit.getPluginManager().isPluginEnabled("Worlds");
        boolean allowCreation = getConfig().getBoolean("allow-world-creation", false);
        if (worldsOwnsRegistry || !allowCreation || isNamespaceWorld(worldName)) {
            callback.accept(null);
            return;
        }

        // World creation is a global operation on Folia. It is permitted only for
        // explicit, non-namespaced legacy worlds and never for TaskForge dimensions.
        Bukkit.getGlobalRegionScheduler().execute(this, () -> {
            World reloaded = findLoadedWorld(worldName);
            if (reloaded != null) {
                callback.accept(reloaded);
                return;
            }

            File folder = new File(Bukkit.getWorldContainer(), worldName);
            boolean exists = folder.isDirectory();
            World created = Bukkit.createWorld(new WorldCreator(worldName).environment(World.Environment.NORMAL));
            if (created == null) {
                getLogger().warning("Failed to load/create legacy world: " + worldName);
            } else {
                getLogger().info((exists ? "Loaded" : "Created") + " legacy world: " + created.getName());
            }
            callback.accept(created);
        });
    }

    private Location readLocation(String path, World world) {
        ConfigurationSection section = getConfig().getConfigurationSection(path);
        double x = section != null ? section.getDouble("x", 0.5) : 0.5;
        double y = section != null ? section.getDouble("y", 100.0) : 100.0;
        double z = section != null ? section.getDouble("z", 0.5) : 0.5;
        float yaw = section != null ? (float) section.getDouble("yaw", 0.0) : 0.0f;
        float pitch = section != null ? (float) section.getDouble("pitch", 0.0) : 0.0f;
        return new Location(world, x, y, z, yaw, pitch);
    }

    private void send(CommandSender sender, String message) {
        if (sender instanceof Player player) {
            player.getScheduler().execute(this, () -> player.sendMessage(message), () -> { }, 1L);
        } else {
            sender.sendMessage(message);
        }
    }

    private void teleport(Player player, World target, String locationPath) {
        if (target == null) {
            send(player, ChatColor.RED + "Target world is not loaded. Load it with the Worlds plugin first.");
            return;
        }

        Location location = readLocation(locationPath, target);
        player.teleportAsync(location).whenComplete((success, error) -> {
            if (error != null || !Boolean.TRUE.equals(success)) {
                send(player, ChatColor.RED + "Teleport failed.");
            } else {
                send(player, ChatColor.GREEN + "Teleported to " + target.getKey());
            }
        });
    }

    @Override
    public boolean onCommand(CommandSender sender, Command command, String label, String[] args) {
        String commandName = command.getName().toLowerCase(Locale.ROOT);

        if (commandName.equals("loadworld")) {
            if (!sender.hasPermission("worldloader.load")) {
                send(sender, ChatColor.RED + "No permission: worldloader.load");
                return true;
            }
            if (args.length != 1) {
                send(sender, ChatColor.YELLOW + "Usage: /loadworld <world>");
                return true;
            }

            String requested = args[0];
            resolveOrLoadWorld(requested, world -> {
                if (world != null) {
                    send(sender, ChatColor.GREEN + "World available: " + world.getKey());
                } else {
                    send(sender, ChatColor.RED + "World is not loaded: " + requested
                        + ". For namespace worlds use Worlds/worlds.dat; this plugin will not create a vanilla substitute.");
                }
            });
            return true;
        }

        if (!(sender instanceof Player player)) {
            sender.sendMessage("You must be a player to use this command.");
            return true;
        }
        if (!player.hasPermission("worldloader.tp")) {
            send(player, ChatColor.RED + "No permission: worldloader.tp");
            return true;
        }

        if (commandName.equals("tpsurv")) {
            String survival = getConfig().getString("survival-world", "owp:overworld");
            resolveOrLoadWorld(survival, world -> teleport(player, world, "survival-spawn"));
            return true;
        }

        if (commandName.equals("tplobby")) {
            String lobby = getConfig().getString("lobby-world", "minecraft:overworld");
            resolveOrLoadWorld(lobby, world -> teleport(player, world, "lobby-spawn"));
            return true;
        }

        return false;
    }

    @Override
    public List<String> onTabComplete(CommandSender sender, Command command, String alias, String[] args) {
        if (!command.getName().equalsIgnoreCase("loadworld") || args.length != 1) return Collections.emptyList();
        String prefix = args[0].toLowerCase(Locale.ROOT);
        List<String> worlds = new ArrayList<>();
        for (World world : Bukkit.getWorlds()) {
            String key = world.getKey().toString();
            if (key.toLowerCase(Locale.ROOT).startsWith(prefix)) worlds.add(key);
        }
        worlds.sort(String::compareToIgnoreCase);
        return worlds;
    }
}
