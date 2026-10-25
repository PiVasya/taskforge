package me.vasya.custommobtweaks.combat;

/** Pure combat rules, also exercised without a running Minecraft server. */
public final class CombatMath {
    private CombatMath() { }

    public static boolean belowHealthThreshold(double health, double maximum, double fraction) {
        return Double.isFinite(health) && Double.isFinite(maximum) && Double.isFinite(fraction)
                && maximum > 0.0 && fraction > 0.0 && fraction <= 1.0
                && health > 0.0 && health < maximum * fraction;
    }

    public static double clamp(double value, double minimum, double maximum, double fallback) {
        return Double.isFinite(value) ? Math.max(minimum, Math.min(maximum, value)) : fallback;
    }

    /** Slab intersection over a finite movement segment, including its endpoints. */
    public static boolean segmentIntersectsBox(double ax, double ay, double az,
                                               double bx, double by, double bz,
                                               double minX, double minY, double minZ,
                                               double maxX, double maxY, double maxZ) {
        double[] a = {ax, ay, az}, b = {bx, by, bz};
        double[] min = {minX, minY, minZ}, max = {maxX, maxY, maxZ};
        double enter = 0.0, leave = 1.0;
        for (int axis = 0; axis < 3; axis++) {
            if (!Double.isFinite(a[axis]) || !Double.isFinite(b[axis])
                    || !Double.isFinite(min[axis]) || !Double.isFinite(max[axis]) || min[axis] > max[axis]) return false;
            double delta = b[axis] - a[axis];
            if (Math.abs(delta) < 1.0e-12) {
                if (a[axis] < min[axis] || a[axis] > max[axis]) return false;
            } else {
                double t1 = (min[axis] - a[axis]) / delta, t2 = (max[axis] - a[axis]) / delta;
                enter = Math.max(enter, Math.min(t1, t2));
                leave = Math.min(leave, Math.max(t1, t2));
                if (enter > leave) return false;
            }
        }
        return true;
    }
}
