package com.example.worldloader;

import org.bukkit.*;
import org.bukkit.command.*;
import org.bukkit.configuration.ConfigurationSection;
import org.bukkit.entity.Player;
import org.bukkit.generator.ChunkGenerator;
import org.bukkit.plugin.java.JavaPlugin;

import java.io.File;
import java.util.Locale;
import java.util.Objects;

public final class WorldLoaderFolia extends JavaPlugin implements CommandExecutor, TabCompleter {

    @Override
    public void onEnable() {
        saveDefaultConfig();

        // Register commands
        Objects.requireNonNull(getCommand("loadworld")).setExecutor(this);
        Objects.requireNonNull(getCommand("tpsurv")).setExecutor(this);
        Objects.requireNonNull(getCommand("tplobby")).setExecutor(this);

        Objects.requireNonNull(getCommand("loadworld")).setTabCompleter(this);

        if (getConfig().getBoolean("autoload-survival-on-start", true)) {
            String survival = getConfig().getString("survival-world", "survival");
            // Load in a safe, synchronous way. On Folia, world creation/loading is allowed here.
            tryLoadWorld(survival);
        }

        getLogger().info("WorldLoaderFolia enabled.");
    }

    private World tryLoadWorld(String worldName) {
        worldName = worldName.trim();
        World w = Bukkit.getWorld(worldName);
        if (w != null) return w;

        // If folder exists, createWorld will load it. If not, it will generate a new world.
        File folder = new File(Bukkit.getWorldContainer(), worldName);
        boolean exists = folder.exists();

        WorldCreator creator = new WorldCreator(worldName);
        creator.environment(World.Environment.NORMAL);

        // Important: do NOT set a custom generator here (we want "normal" world).
        // If you want custom seed, you should pre-generate the folder (as you did),
        // or set level-seed when creating a fresh world on a separate server run.

        w = Bukkit.createWorld(creator);

        if (w == null) {
            getLogger().warning("Failed to load/create world: " + worldName);
            return null;
        }

        getLogger().info((exists ? "Loaded" : "Created") + " world: " + w.getName());
        return w;
    }

    private Location readLocation(String path, World w) {
        ConfigurationSection sec = getConfig().getConfigurationSection(path);
        double x = sec != null ? sec.getDouble("x", 0.5) : 0.5;
        double y = sec != null ? sec.getDouble("y", 100.0) : 100.0;
        double z = sec != null ? sec.getDouble("z", 0.5) : 0.5;
        float yaw = sec != null ? (float) sec.getDouble("yaw", 0.0) : 0.0f;
        float pitch = sec != null ? (float) sec.getDouble("pitch", 0.0) : 0.0f;
        return new Location(w, x, y, z, yaw, pitch);
    }

    private void teleportAsync(Player p, World target, String locPath) {
        if (target == null) {
            p.sendMessage(ChatColor.RED + "Target world is not loaded.");
            return;
        }
        Location loc = readLocation(locPath, target);

        // Folia-friendly: use teleportAsync
        p.teleportAsync(loc).thenAccept(success -> {
            if (success) {
                p.sendMessage(ChatColor.GREEN + "Teleported to " + target.getName());
            } else {
                p.sendMessage(ChatColor.RED + "Teleport failed.");
            }
        });
    }

    @Override
    public boolean onCommand(CommandSender sender, Command command, String label, String[] args) {

        String cmd = command.getName().toLowerCase(Locale.ROOT);

        if (cmd.equals("loadworld")) {
            if (!sender.hasPermission("worldloader.load")) {
                sender.sendMessage(ChatColor.RED + "No permission: worldloader.load");
                return true;
            }
            if (args.length < 1) {
                sender.sendMessage(ChatColor.YELLOW + "Usage: /loadworld <world>");
                return true;
            }
            String worldName = args[0];
            World w = tryLoadWorld(worldName);
            if (w != null) {
                sender.sendMessage(ChatColor.GREEN + "World loaded: " + w.getName());
            } else {
                sender.sendMessage(ChatColor.RED + "World failed to load: " + worldName);
            }
            return true;
        }

        if (!(sender instanceof Player p)) {
            sender.sendMessage("You must be a player to use this command.");
            return true;
        }

        if (cmd.equals("tpsurv")) {
            if (!p.hasPermission("worldloader.tp")) {
                p.sendMessage(ChatColor.RED + "No permission: worldloader.tp");
                return true;
            }
            String survival = getConfig().getString("survival-world", "survival");
            World w = tryLoadWorld(survival);
            teleportAsync(p, w, "survival-spawn");
            return true;
        }

        if (cmd.equals("tplobby")) {
            if (!p.hasPermission("worldloader.tp")) {
                p.sendMessage(ChatColor.RED + "No permission: worldloader.tp");
                return true;
            }
            String lobby = getConfig().getString("lobby-world", "world");
            World w = Bukkit.getWorld(lobby);
            if (w == null) w = tryLoadWorld(lobby);
            teleportAsync(p, w, "lobby-spawn");
            return true;
        }

        return false;
    }

    @Override
    public java.util.List<String> onTabComplete(CommandSender sender, Command command, String alias, String[] args) {
        if (!command.getName().equalsIgnoreCase("loadworld")) return java.util.Collections.emptyList();
        if (args.length == 1) {
            String prefix = args[0].toLowerCase(Locale.ROOT);
            java.util.List<String> worlds = new java.util.ArrayList<>();
            for (World w : Bukkit.getWorlds()) {
                String name = w.getName();
                if (name.toLowerCase(Locale.ROOT).startsWith(prefix)) worlds.add(name);
            }
            // Also suggest folders in world container
            File wc = Bukkit.getWorldContainer();
            File[] files = wc.listFiles(File::isDirectory);
            if (files != null) {
                for (File f : files) {
                    String name = f.getName();
                    if (name.equalsIgnoreCase("plugins") || name.equalsIgnoreCase("logs") || name.equalsIgnoreCase("cache")) continue;
                    if (name.toLowerCase(Locale.ROOT).startsWith(prefix) && !worlds.contains(name)) worlds.add(name);
                }
            }
            worlds.sort(String::compareToIgnoreCase);
            return worlds;
        }
        return java.util.Collections.emptyList();
    }
}
