package me.vasya.custommobtweaks;

import org.bukkit.command.Command;
import org.bukkit.command.CommandExecutor;
import org.bukkit.command.CommandSender;
import org.bukkit.command.TabCompleter;
import org.bukkit.entity.Player;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

public final class MobTweaksCommand implements CommandExecutor, TabCompleter {
    private final CustomMobTweaksPlugin plugin;
    private final HappyGhastBomberListener bomberListener;

    public MobTweaksCommand(CustomMobTweaksPlugin plugin, HappyGhastBomberListener bomberListener) {
        this.plugin = plugin;
        this.bomberListener = bomberListener;
    }

    @Override
    public boolean onCommand(@NotNull CommandSender sender, @NotNull Command command, @NotNull String label, @NotNull String[] args) {
        if (args.length == 0) {
            sendHelp(sender, label);
            return true;
        }

        switch (args[0].toLowerCase(Locale.ROOT)) {
            case "reload" -> {
                if (!sender.hasPermission("custommobtweaks.admin")) {
                    sender.sendMessage("§cНет права custommobtweaks.admin.");
                    return true;
                }
                plugin.reloadPluginConfig();
                sender.sendMessage("§aКонфигурация CustomMobTweaks перезагружена.");
            }
            case "modules" -> {
                if (!sender.hasPermission("custommobtweaks.admin")) {
                    sender.sendMessage("§cНет права custommobtweaks.admin.");
                    return true;
                }
                sender.sendMessage("§6Модули CustomMobTweaks:");
                for (String module : List.of(
                        "harder-creaking", "harder-breeze", "harder-bogged", "harder-armadillo",
                        "harder-stray", "illusioner-spawn", "trident-zombie", "happy-ghast-bomber",
                        "lava-damage", "dry-weapon", "freezing-snowball")) {
                    sender.sendMessage("§7- §f" + module + ": " + (plugin.enabled(module) ? "§aON" : "§cOFF"));
                }
            }
            case "bomber" -> handleBomber(sender, args);
            default -> sendHelp(sender, label);
        }
        return true;
    }

    private void handleBomber(CommandSender sender, String[] args) {
        if (!(sender instanceof Player player)) {
            sender.sendMessage("§cЭта команда доступна только игроку.");
            return;
        }
        if (!sender.hasPermission("custommobtweaks.bomber")) {
            sender.sendMessage("§cНет права custommobtweaks.bomber.");
            return;
        }
        if (args.length < 2) {
            sender.sendMessage("§eИспользование: /custommobtweaks bomber <on|off|status>");
            return;
        }
        switch (args[1].toLowerCase(Locale.ROOT)) {
            case "on", "enable", "start" -> bomberListener.activate(player);
            case "off", "disable", "stop" -> bomberListener.deactivate(player);
            case "status" -> sender.sendMessage(bomberListener.isActive(player)
                    ? "§aРежим бомбардировщика включён."
                    : "§eРежим бомбардировщика выключен.");
            default -> sender.sendMessage("§eИспользование: /custommobtweaks bomber <on|off|status>");
        }
    }

    private void sendHelp(CommandSender sender, String label) {
        sender.sendMessage("§6CustomMobTweaks 2.0.0");
        sender.sendMessage("§e/" + label + " modules §7— список модулей");
        sender.sendMessage("§e/" + label + " reload §7— перезагрузить config.yml");
        sender.sendMessage("§e/" + label + " bomber <on|off|status> §7— счастливый гаст-бомбардировщик");
    }

    @Override
    public @Nullable List<String> onTabComplete(@NotNull CommandSender sender, @NotNull Command command,
                                                 @NotNull String alias, @NotNull String[] args) {
        if (args.length == 1) {
            return filter(List.of("reload", "modules", "bomber"), args[0]);
        }
        if (args.length == 2 && args[0].equalsIgnoreCase("bomber")) {
            return filter(List.of("on", "off", "status"), args[1]);
        }
        return List.of();
    }

    private List<String> filter(List<String> values, String input) {
        String prefix = input.toLowerCase(Locale.ROOT);
        List<String> result = new ArrayList<>();
        for (String value : values) {
            if (value.startsWith(prefix)) {
                result.add(value);
            }
        }
        return result;
    }
}
