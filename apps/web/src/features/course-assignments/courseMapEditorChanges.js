// React Flow emits position/size events while mounting or reconciling nodes.
// They must not turn a fresh server snapshot into an unsaved local draft.
export function hasIntentionalNodeChanges(changes, previousNodes, {
  ready = false,
  dragging = false,
  keyboardMove = false,
} = {}) {
  if (!ready || !Array.isArray(changes) || !changes.length) return false;
  const byId = new Map((previousNodes || []).map((node) => [String(node.id), node]));
  return changes.some((change) => {
    const current = byId.get(String(change?.id || ''));
    if (!current) return false;
    if (change.type === 'remove') return true;
    if (change.type !== 'position' || !change.position) return false;
    if (!dragging && !keyboardMove && change.dragging !== true) return false;
    const before = current.position || {};
    const after = change.position;
    return Number.isFinite(Number(after.x)) && Number.isFinite(Number(after.y))
      && (Number(before.x) !== Number(after.x) || Number(before.y) !== Number(after.y));
  });
}

// The old working draft is disposable only after an authoritative fetch succeeds.
// A slow or failed request must not destroy local edits, and a newer request wins.
export async function loadFreshEditorMap({ fetchSnapshot, discardDraft, applySnapshot, isCurrent }) {
  const snapshot = await fetchSnapshot();
  if (!isCurrent()) return false;
  await discardDraft();
  if (!isCurrent()) return false;
  return applySnapshot(snapshot);
}
