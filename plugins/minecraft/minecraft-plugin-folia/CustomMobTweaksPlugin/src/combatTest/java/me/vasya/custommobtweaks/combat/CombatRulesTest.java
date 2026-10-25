package me.vasya.custommobtweaks.combat;

import java.util.Random;

/** Dependency-free executable regression tests; assertions do not depend on the JVM -ea flag. */
public final class CombatRulesTest {
    private static int checks;

    public static void main(String[] args) {
        check(!CombatMath.belowHealthThreshold(8.0, 20.0, 0.4), "exactly 40% must not trigger");
        check(CombatMath.belowHealthThreshold(7.99, 20.0, 0.4), "strictly below 40% must trigger");
        check(!CombatMath.belowHealthThreshold(0.0, 20.0, 0.4), "dead mob must not call guards");
        check(!CombatMath.belowHealthThreshold(-1.0, 20.0, 0.4), "lethal damage must not call guards");
        check(!CombatMath.belowHealthThreshold(8.01, 20.0, 0.4), "above threshold must not trigger");
        check(!CombatMath.belowHealthThreshold(16.0, 40.0, 0.4), "scaled exact threshold");
        check(CombatMath.belowHealthThreshold(15.0, 40.0, 0.4), "modified maximum HP is respected");
        check(!CombatMath.belowHealthThreshold(1.0, 0.0, 0.4), "invalid maximum");
        check(!CombatMath.belowHealthThreshold(Double.NaN, 20.0, 0.4), "NaN health");
        check(!CombatMath.belowHealthThreshold(1.0, Double.POSITIVE_INFINITY, 0.4), "infinite maximum");
        check(!CombatMath.belowHealthThreshold(1.0, 20.0, Double.POSITIVE_INFINITY), "invalid threshold");
        check(CombatMath.clamp(Double.NaN, 0.0, 2.0, 1.35) == 1.35, "NaN speed fallback");
        check(CombatMath.clamp(50.0, 0.0, 2.0, 1.35) == 2.0, "upper config bound");
        check(CombatMath.clamp(-3.0, 0.0, 2.0, 1.35) == 0.0, "lower config bound");
        check(CombatMath.clamp(1.35, 0.0, 2.0, 1.0) == 1.35, "valid config unchanged");
        check(hit(-2, 0, 0, 2, 0, 0), "fast dash crossing whole victim");
        check(hit(2, 0, 0, -2, 0, 0), "reverse direction");
        check(!hit(-2, 3, 0, 2, 3, 0), "parallel miss");
        check(!hit(-4, 0, 0, -2, 0, 0), "finite segment cannot hit beyond its end");
        check(hit(0, 0, 0, 0, 0, 0), "stationary overlap");
        check(!hit(2, 2, 2, 2, 2, 2), "stationary miss");
        check(hit(-3, -3, -3, 3, 3, 3), "diagonal three-dimensional dash");
        check(hit(-2, 1, 1, 2, 1, 1), "grazing edge");
        check(hit(-2, 0, 0, -1, 0, 0), "endpoint contact");
        check(!hit(Double.NaN, 0, 0, 0, 0, 0), "NaN movement cannot hit");
        check(!hit(Double.POSITIVE_INFINITY, 0, 0, 0, 0, 0), "infinite movement cannot hit");
        check(!CombatMath.segmentIntersectsBox(0, 0, 0, 1, 1, 1, 2, 2, 2, 1, 1, 1), "inverted box");
        Random random = new Random(281025L);
        for (int i = 0; i < 2000; i++) {
            double ax = random.nextDouble() * 12 - 6, ay = random.nextDouble() * 12 - 6, az = random.nextDouble() * 12 - 6;
            double bx = random.nextDouble() * 12 - 6, by = random.nextDouble() * 12 - 6, bz = random.nextDouble() * 12 - 6;
            boolean forward = hit(ax, ay, az, bx, by, bz);
            check(forward == hit(bx, by, bz, ax, ay, az), "segment intersection symmetry");
            double t = random.nextDouble();
            double x = ax + (bx - ax) * t, y = ay + (by - ay) * t, z = az + (bz - az) * t;
            check(CombatMath.segmentIntersectsBox(ax, ay, az, bx, by, bz,
                    x - 0.01, y - 0.01, z - 0.01, x + 0.01, y + 0.01, z + 0.01), "a point along the segment must be hit");
        }
        System.out.println("PASS: " + checks + " combat rule/geometry checks");
    }

    private static boolean hit(double ax, double ay, double az, double bx, double by, double bz) {
        return CombatMath.segmentIntersectsBox(ax, ay, az, bx, by, bz, -1, -1, -1, 1, 1, 1);
    }

    private static void check(boolean condition, String message) {
        checks++;
        if (!condition) throw new AssertionError(message);
    }
}
