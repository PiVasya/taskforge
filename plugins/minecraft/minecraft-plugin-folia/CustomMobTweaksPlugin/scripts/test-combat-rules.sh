#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
javac -encoding UTF-8 -d "$work" \
  "$root/src/main/java/me/vasya/custommobtweaks/combat/CombatMath.java" \
  "$root/src/main/java/me/vasya/custommobtweaks/combat/SpiderCloneRules.java" \
  "$root/src/combatTest/java/me/vasya/custommobtweaks/combat/SpiderCloneRulesTest.java" \
  "$root/src/combatTest/java/me/vasya/custommobtweaks/combat/CombatRulesTest.java"
java -cp "$work" me.vasya.custommobtweaks.combat.CombatRulesTest
