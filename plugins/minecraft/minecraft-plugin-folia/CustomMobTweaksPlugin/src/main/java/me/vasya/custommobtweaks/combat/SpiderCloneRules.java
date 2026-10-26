package me.vasya.custommobtweaks.combat;

/** Basic checks shared by the spider bite handler and dependency-free tests. */
public final class SpiderCloneRules {
    private SpiderCloneRules() {
    }

    public static boolean shouldSpawn(boolean enabled, boolean realSpider, boolean markedCopy,
                                      boolean meleeBite, boolean livingVictim, double finalDamage) {
        return enabled && realSpider && !markedCopy && meleeBite && livingVictim
                && Double.isFinite(finalDamage) && finalDamage > 0.0D;
    }

    public static double scale(double configured) {
        if (!Double.isFinite(configured)) return 0.35D;
        return Math.max(0.1D, Math.min(1.0D, configured));
    }

    public static long lifetimeTicks(long configured) {
        return Math.max(1L, Math.min(1200L, configured));
    }
}
