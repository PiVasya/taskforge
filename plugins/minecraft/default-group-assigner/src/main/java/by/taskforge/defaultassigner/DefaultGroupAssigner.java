package by.taskforge.defaultassigner;

import org.bukkit.Bukkit;
import org.bukkit.command.ConsoleCommandSender;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.Listener;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.plugin.java.JavaPlugin;

public final class DefaultGroupAssigner extends JavaPlugin implements Listener {

    @Override
    public void onEnable() {
        saveDefaultConfig();
        Bukkit.getPluginManager().registerEvents(this, this);
        getLogger().info("DefaultGroupAssigner enabled");
    }

    @EventHandler
    public void onJoin(PlayerJoinEvent e) {
        Player p = e.getPlayer();

        if (getConfig().getBoolean("skipOps", true) && p.isOp()) {
            return;
        }

        if (p.hasPermission("defaultassigner.bypass")) {
            return;
        }

        String group = getConfig().getString("group", "default");
        int delay = Math.max(1, getConfig().getInt("delayTicks", 20));
        String template = getConfig().getString("commandTemplate", "fperm user addgroup {player} {group}");

        String cmd = template
                .replace("{player}", p.getName())
                .replace("{group}", group);

        ConsoleCommandSender console = Bukkit.getConsoleSender();

        // Folia doesn't support the old Bukkit scheduler (CraftScheduler). Use the Folia schedulers instead.
        Bukkit.getGlobalRegionScheduler().runDelayed(this, scheduledTask -> {
            boolean ok = Bukkit.dispatchCommand(console, cmd);
            if (!ok) {
                getLogger().warning("Command failed: " + cmd);
            }
        }, delay);
    }
}
