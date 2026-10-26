package me.vasya.custommobtweaks.combat;

/** Dependency-free edge cases for bite-triggered, non-recursive temporary copies. */
public final class SpiderCloneRulesTest {
    private SpiderCloneRulesTest() {
    }

    public static int run() {
        int checks = 0;
        check(SpiderCloneRules.shouldSpawn(true, true, false, true, true, 1.0D), "regular bite"); checks++;
        check(SpiderCloneRules.shouldSpawn(true, true, false, true, true, 2.0D), "cave spider bite"); checks++;
        check(!SpiderCloneRules.shouldSpawn(true, true, true, true, true, 2.0D), "clone must not reproduce"); checks++;
        check(!SpiderCloneRules.shouldSpawn(false, true, false, true, true, 2.0D), "disabled"); checks++;
        check(!SpiderCloneRules.shouldSpawn(true, false, false, true, true, 2.0D), "non-spider"); checks++;
        check(!SpiderCloneRules.shouldSpawn(true, true, false, false, true, 2.0D), "non-bite damage"); checks++;
        check(!SpiderCloneRules.shouldSpawn(true, true, false, true, false, 2.0D), "non-living victim"); checks++;
        check(!SpiderCloneRules.shouldSpawn(true, true, false, true, true, 0.0D), "blocked hit"); checks++;
        check(!SpiderCloneRules.shouldSpawn(true, true, false, true, true, -1.0D), "invalid negative damage"); checks++;
        check(!SpiderCloneRules.shouldSpawn(true, true, false, true, true, Double.NaN), "NaN"); checks++;
        check(SpiderCloneRules.scale(0.35D) == 0.35D, "tiny default scale"); checks++;
        check(SpiderCloneRules.scale(-1.0D) == 0.1D, "minimum scale"); checks++;
        check(SpiderCloneRules.scale(20.0D) == 1.0D, "maximum scale"); checks++;
        check(SpiderCloneRules.scale(Double.NaN) == 0.35D, "NaN scale default"); checks++;
        check(SpiderCloneRules.lifetimeTicks(100L) == 100L, "five-second default"); checks++;
        check(SpiderCloneRules.lifetimeTicks(0L) == 1L, "minimum lifespan"); checks++;
        check(SpiderCloneRules.lifetimeTicks(99999L) == 1200L, "bounded lifespan"); checks++;
        return checks;
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}
