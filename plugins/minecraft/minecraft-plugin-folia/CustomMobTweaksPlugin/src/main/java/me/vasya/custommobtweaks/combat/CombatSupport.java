package me.vasya.custommobtweaks.combat;

import me.vasya.custommobtweaks.CustomMobTweaksPlugin;
import org.bukkit.Bukkit;
import org.bukkit.FluidCollisionMode;
import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.entity.ArmorStand;
import org.bukkit.entity.Entity;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Mob;
import org.bukkit.entity.Player;
import org.bukkit.entity.Projectile;
import org.bukkit.inventory.EntityEquipment;
import org.bukkit.util.BoundingBox;
import org.bukkit.util.Vector;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.function.BooleanSupplier;
import java.util.function.Consumer;

public final class CombatSupport {
    private CombatSupport() { }

    public static boolean combatant(LivingEntity entity) {
        if (!entity.isValid() || entity.isDead() || entity.getHealth() <= 0.0 || entity.isInvulnerable()
                || entity instanceof ArmorStand) return false;
        return !(entity instanceof Player player) || (player.isOnline()
                && (player.getGameMode() == GameMode.SURVIVAL || player.getGameMode() == GameMode.ADVENTURE));
    }

    public static boolean usableTarget(Mob mob, LivingEntity target, double range) {
        return target != null && Bukkit.isOwnedByCurrentRegion(target) && combatant(target)
                && mob.getWorld().equals(target.getWorld())
                && mob.getLocation().distanceSquared(target.getLocation()) <= range * range;
    }

    /** Read only owned, loaded chunks; never load terrain or read another region's entity state. */
    public static List<LivingEntity> nearbyOwned(Location center, double radius) {
        World world = center.getWorld();
        List<LivingEntity> result = new ArrayList<>();
        int minX = ((int) Math.floor(center.getX() - radius)) >> 4;
        int maxX = ((int) Math.floor(center.getX() + radius)) >> 4;
        int minZ = ((int) Math.floor(center.getZ() - radius)) >> 4;
        int maxZ = ((int) Math.floor(center.getZ() + radius)) >> 4;
        for (int x = minX; x <= maxX; x++) {
            for (int z = minZ; z <= maxZ; z++) {
                if (!Bukkit.isOwnedByCurrentRegion(world, x, z) || !world.isChunkLoaded(x, z)) continue;
                for (Entity entity : world.getChunkAt(x, z).getEntities()) {
                    if (entity instanceof LivingEntity living && Bukkit.isOwnedByCurrentRegion(living)
                            && living.isValid() && living.getWorld().equals(world)
                            && living.getLocation().distanceSquared(center) <= radius * radius) result.add(living);
                }
            }
        }
        return result;
    }

    /** Used for a one-off alarm: each remote chunk is inspected on its own region. */
    public static void forEachNearby(CustomMobTweaksPlugin plugin, Location center, double radius,
                                     BooleanSupplier valid, Consumer<LivingEntity> action) {
        World world = center.getWorld();
        int minX = ((int) Math.floor(center.getX() - radius)) >> 4;
        int maxX = ((int) Math.floor(center.getX() + radius)) >> 4;
        int minZ = ((int) Math.floor(center.getZ() - radius)) >> 4;
        int maxZ = ((int) Math.floor(center.getZ() + radius)) >> 4;
        for (int x = minX; x <= maxX; x++) {
            for (int z = minZ; z <= maxZ; z++) {
                final int cx = x, cz = z;
                // isChunkLoaded does not load a chunk. The callback checks again after scheduling.
                if (!world.isChunkLoaded(cx, cz)) continue;
                Runnable visit = () -> {
                    if (!valid.getAsBoolean() || !world.isChunkLoaded(cx, cz)) return;
                    for (Entity entity : world.getChunkAt(cx, cz).getEntities()) {
                        if (entity instanceof LivingEntity living && Bukkit.isOwnedByCurrentRegion(living)
                                && living.isValid() && living.getWorld().equals(world)
                                && living.getLocation().distanceSquared(center) <= radius * radius) action.accept(living);
                    }
                };
                if (Bukkit.isOwnedByCurrentRegion(world, cx, cz)) visit.run();
                else Bukkit.getRegionScheduler().execute(plugin, world, cx, cz, visit);
            }
        }
    }

    public static Player nearestPlayer(Mob mob, double range) {
        return nearbyOwned(mob.getLocation(), range).stream()
                .filter(Player.class::isInstance).map(Player.class::cast)
                .filter(CombatSupport::combatant)
                .filter(mob::hasLineOfSight)
                .min(Comparator.comparingDouble(player -> player.getLocation().distanceSquared(mob.getLocation())))
                .orElse(null);
    }

    public static Player playerAttacker(Entity source) {
        if (source instanceof Player player) return player;
        if (source instanceof Projectile projectile && projectile.getShooter() instanceof Player player) return player;
        return null;
    }

    public static boolean holds(Mob mob, Material material) {
        EntityEquipment gear = mob.getEquipment();
        return gear != null && (gear.getItemInMainHand().getType() == material
                || gear.getItemInOffHand().getType() == material);
    }

    public static boolean lineOfSight(Location from, Location to) {
        if (!from.getWorld().equals(to.getWorld())) return false;
        Vector delta = to.toVector().subtract(from.toVector());
        double length = delta.length();
        if (length < 0.001) return true;
        if (!ownsArea(from, to)) return false;
        return from.getWorld().rayTraceBlocks(from, delta.multiply(1.0 / length), length,
                FluidCollisionMode.NEVER, true) == null;
    }

    public static boolean ownsArea(Location a, Location b) {
        World world = a.getWorld();
        int minX = Math.min(a.getBlockX(), b.getBlockX()) >> 4;
        int maxX = Math.max(a.getBlockX(), b.getBlockX()) >> 4;
        int minZ = Math.min(a.getBlockZ(), b.getBlockZ()) >> 4;
        int maxZ = Math.max(a.getBlockZ(), b.getBlockZ()) >> 4;
        for (int x = minX; x <= maxX; x++) for (int z = minZ; z <= maxZ; z++) {
            if (!Bukkit.isOwnedByCurrentRegion(world, x, z) || !world.isChunkLoaded(x, z)) return false;
        }
        return true;
    }

    /** Full entity bounding box, real support, liquid/hazard and world-border checks. */
    public static boolean safeFeet(Mob mob, Location destination) {
        World world = mob.getWorld();
        if (!destination.getWorld().equals(world) || !world.getWorldBorder().isInside(destination)) return false;
        BoundingBox box = mob.getBoundingBox().clone();
        Vector translation = destination.toVector().subtract(mob.getLocation().toVector());
        box.shift(translation);
        if (box.getMinY() <= world.getMinHeight() || box.getMaxY() >= world.getMaxHeight()) return false;
        Location min = new Location(world, box.getMinX() - 0.01, box.getMinY() - 1.0, box.getMinZ() - 0.01);
        Location max = new Location(world, box.getMaxX() + 0.01, box.getMaxY(), box.getMaxZ() + 0.01);
        if (!ownsArea(min, max) || !world.getWorldBorder().isInside(min)
                || !world.getWorldBorder().isInside(max)) return false;
        for (int x = min.getBlockX(); x <= max.getBlockX(); x++) {
            for (int z = min.getBlockZ(); z <= max.getBlockZ(); z++) {
                for (int y = (int) Math.floor(box.getMinY()); y <= (int) Math.floor(box.getMaxY() - 0.001); y++) {
                    Block block = world.getBlockAt(x, y, z);
                    if (block.isLiquid() || hazardous(block.getType())) return false;
                }
            }
        }
        Block floor = world.getBlockAt(destination.getBlockX(), destination.getBlockY() - 1, destination.getBlockZ());
        return floor.getType().isSolid() && !hazardous(floor.getType())
                && !floor.getCollisionShape().getBoundingBoxes().isEmpty() && !mob.collidesAt(destination);
    }

    private static boolean hazardous(Material type) {
        return switch (type) {
            case LAVA, FIRE, SOUL_FIRE, MAGMA_BLOCK, CACTUS, CAMPFIRE, SOUL_CAMPFIRE,
                    POWDER_SNOW, SWEET_BERRY_BUSH, WITHER_ROSE, END_PORTAL, NETHER_PORTAL -> true;
            default -> false;
        };
    }

    /** Use walkable paths, not velocity impulses that send a fleeing mob through walls. */
    public static boolean moveAway(Mob mob, Location threat, double distance, double speed) {
        Location origin = mob.getLocation();
        if (!origin.getWorld().equals(threat.getWorld())) return false;
        Vector away = origin.toVector().subtract(threat.toVector()).setY(0.0);
        if (away.lengthSquared() < 0.001) away = new Vector(1.0, 0.0, 0.0);
        away.normalize();
        for (double angle : new double[] {0.0, 0.6, -0.6, 1.2, -1.2}) {
            Vector step = away.clone().rotateAroundY(angle).multiply(distance);
            Location sample = origin.clone().add(step);
            for (int dy : new int[] {0, 1, -1, -2}) {
                Location feet = new Location(origin.getWorld(), sample.getBlockX() + 0.5,
                        origin.getBlockY() + dy, sample.getBlockZ() + 0.5);
                if (!safeFeet(mob, feet)) continue;
                var path = mob.getPathfinder().findPath(feet);
                if (path != null && path.getFinalPoint() != null
                        && path.getFinalPoint().distanceSquared(threat) > origin.distanceSquared(threat) + 1.0
                        && mob.getPathfinder().moveTo(path, speed)) return true;
            }
        }
        return false;
    }
}
