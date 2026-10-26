// Shared by the modal and the recovered job history; contains no browser state.
export const isTransferActive = (job) => ['queued', 'running'].includes(job?.state);
export const TRANSFER_EXPORT_PRESETS = Object.freeze({
  layout: { includeIds: true, includeContent: false, includeChecks: false, includeVisibility: false,
    includeConnections: false, includeConnectionAccess: false, includeLayout: true, includeGuide: false },
  structure: { includeIds: true, includeContent: false, includeChecks: false, includeVisibility: true,
    includeConnections: true, includeConnectionAccess: true, includeLayout: true, includeGuide: false },
  content: { includeIds: true, includeContent: true, includeChecks: false, includeVisibility: false,
    includeConnections: false, includeConnectionAccess: false, includeLayout: false, includeGuide: false },
  full: { includeIds: true, includeContent: true, includeChecks: true, includeVisibility: true,
    includeConnections: true, includeConnectionAccess: true, includeLayout: true, includeGuide: false },
});
export function previewExportOptions(graph, options = {}) {
  const scopes = new Set(Array.isArray(graph?.scopes) ? graph.scopes : ['content', 'checks']);
  return { ...TRANSFER_EXPORT_PRESETS.structure,
    includeContent: options.updateContent !== false && scopes.has('content'),
    includeChecks: options.updateChecks !== false && scopes.has('checks'),
  };
}
export function mergeTransferStatuses(previous, incoming, courseId, userId) {
  const own = (row) => String(row.courseId) === String(courseId) && String(row.userId) === String(userId);
  const rows = new Map(previous.filter(own).map((row) => [row.transferId, row]));
  for (const row of incoming.filter(own)) {
    const old = rows.get(row.transferId);
    const newer = !old || Date.parse(row.updatedAt) > Date.parse(old.updatedAt);
    const sameTime = old && Date.parse(row.updatedAt) === Date.parse(old.updatedAt);
    // A late poll at the same timestamp must not hide a terminal receipt.
    const higherAttempt = (row.attemptCount || 0) > (old?.attemptCount || 0);
    const terminalUpgrade = sameTime && (!isTransferActive(row) || isTransferActive(old));
    if (newer || (sameTime && higherAttempt) || terminalUpgrade) rows.set(row.transferId, row);
  }
  return [...rows.values()].sort((a, b) => Number(isTransferActive(b)) - Number(isTransferActive(a)) || Date.parse(b.createdAt) - Date.parse(a.createdAt)).slice(0, 30);
}
export function transferImportPercent(job, operation) {
  if (operation?.operationId === job?.operationId && operation?.state === 'completed') return 100;
  if (job?.kind === 'map') return Math.min(99, 70 + Math.floor((job.percent || 0) * 29 / 100));
  if (['import', 'resume'].includes(job?.kind)) return Math.min(70, Math.floor((job.percent || 0) * 0.7));
  return Math.max(0, Math.min(100, job?.percent || 0));
}
// Preview metadata is not part of the portable export scopes; it never contains
// descriptions, source code, test inputs or answers.
export function withPreviewMetadata(graph, preflight) {
  const metadata = new Map((preflight?.metadata || []).map((row) => [String(row.id).toLowerCase(), row]));
  return { ...graph, tasks: (graph?.tasks || []).map((row) => {
    const meta = metadata.get(String(row.id).toLowerCase());
    return meta ? { ...row, title: row.title ?? meta.title, type: row.type ?? meta.type } : row;
  }) };
}
