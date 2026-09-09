#!/usr/bin/env bash
set -euo pipefail

links='services/minecraft/api/Endpoints/Links/LinksEndpoints.cs'
death='services/minecraft/api/Endpoints/DeathRecovery/DeathRecoveryEndpoints.cs'
common='services/minecraft/api/Services/Common/MinecraftApiCommonService.cs'
front='apps/web/src/features/settings/SettingsFeature.jsx'
client='apps/web/src/api/minecraftLink.js'
rules='00_AI_READ_THIS_FIRST.md'

fail() {
  echo "minecraft-link invariant failed: $*" >&2
  exit 1
}

for file in "$links" "$death" "$common" "$front" "$client" "$rules"; do
  [ -f "$file" ] || fail "missing $file"
done

if grep -Eq 'Лимит привязок|2/2|confirmedCount[[:space:]]*>=[[:space:]]*2|Minecraft уже привязан\. Сначала отвяжи|Привязки:.*\/2' "$links" "$front"; then
  fail 'numeric Minecraft link cap returned'
fi

grep -Fq 'MapDelete("/api/integrations/minecraft/links/{linkId:guid}"' "$links" \
  || fail 'per-link unlink endpoint is missing'
grep -Fq 'BuildPlayerStatusAsync(active,' "$links" \
  || fail 'plugin status is not tied to the exact resolved link'
grep -Fq 'x.UnlinkedAtUtc == null' "$links" \
  || fail 'active-link filtering is missing'
grep -Fq 'if (activeLinksRemaining == 0)' "$links" \
  || fail 'Minecraft role could be removed while other links remain'
grep -Fq 'await unlinkMinecraft(linkId)' "$front" \
  || fail 'frontend does not remove one link by id'
grep -Fq 'api.delete(`/api/integrations/minecraft/links/${linkId}`)' "$client" \
  || fail 'frontend API does not use per-link unlink'
grep -Fq 'any number of simultaneously active Minecraft' "$rules" \
  || fail 'AI rules do not preserve unlimited-link policy'
grep -Fq 'shared user-level ledger keyed only by TaskForge `UserId`' "$rules" \
  || fail 'AI rules do not preserve shared-balance policy'

if grep -Eq 'RatingTransactions\.(Add|AddAsync|Remove|RemoveRange)|new[[:space:]]+MinecraftRatingTransaction|Delta[[:space:]]*=' "$links"; then
  fail 'link/unlink endpoint writes the Minecraft rating ledger'
fi
grep -Fq 'Where(x => x.UserId == userId)' "$common" \
  || fail 'Minecraft balance is no longer calculated from the TaskForge user ledger'

# Nick fallback must remain ambiguity-safe now that a user may have many links.
grep -Fq '.Distinct()' "$death" || fail 'legacy nickname lookup is not uniqueness-safe'
grep -Fq '.Take(2)' "$death" || fail 'legacy nickname lookup does not detect ambiguity'

echo 'minecraft link invariants ok'
