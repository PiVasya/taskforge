// Pure selectors for the account-intelligence review queue.
// A decision accepted in this browser session remains in place so removing an
// expanded panel cannot unexpectedly pull the administrator to another row.
export const decisionFindingStatus = (decision) => ({
  duplicate: 'confirmed',
  different: 'dismissed',
  ignored: 'ignored',
}[decision] || 'open');

export function duplicateFindingStatus(finding, localAction, operation) {
  const persisted = finding?.status || 'open';
  if (persisted !== 'open' && persisted !== 'merge-queued') return persisted;
  if (operation?.status === 'completed') return 'resolved-by-merge';
  if (operation?.status === 'failed') return 'merge-failed';
  if (operation?.status === 'cancelled') return 'merge-cancelled';
  return persisted === 'open' ? (localAction?.status || persisted) : persisted;
}

export function filterAccountReviewFindings({ findings = [], tab, query = '', verifiedIds = new Set(), blockedIds = new Set(), localActions = {} }) {
  const search = query.trim().toLowerCase();
  return findings.filter((item) => {
    const accountIds = item.kind === 'duplicate'
      ? (item.data?.accounts || []).map((account) => String(account.userId))
      : [String(item.data?.account?.userId || item.primaryUserId || '')];
    const allAnchored = accountIds.length > 0 && accountIds.every((userId) => verifiedIds.has(userId) || blockedIds.has(userId));
    const singleAnchored = item.kind === 'suspicious' && accountIds.some((userId) => verifiedIds.has(userId) || blockedIds.has(userId));
    // Locally handled rows remain visible until the next scan or page reload,
    // including after the server switches a finding to merge-queued/resolved.
    if (tab === 'duplicates' && (item.kind !== 'duplicate' || (!localActions[item.id] && (item.status !== 'open' || allAnchored)))) return false;
    if (tab === 'suspicious' && (item.kind !== 'suspicious' || item.status !== 'open' || singleAnchored)) return false;
    return !search || JSON.stringify(item.data || {}).toLowerCase().includes(search)
      || String(item.findingKey || '').toLowerCase().includes(search);
  });
}
