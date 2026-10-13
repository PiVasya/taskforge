export function importPointerKey(userId, courseId) {
  return `taskforge:import-operation:v1:${userId}:${courseId}`;
}

export function newImportOperationId(cryptoProvider = globalThis.crypto) {
  if (cryptoProvider?.randomUUID) return cryptoProvider.randomUUID();
  if (!cryptoProvider?.getRandomValues) throw new Error('Нужен безопасный контекст браузера для создания operationId.');
  const bytes = cryptoProvider.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 15) | 64;
  bytes[8] = (bytes[8] & 63) | 128;
  const hex = Array.from(bytes, (x) => x.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function isImportPending(operation) {
  return Boolean(operation?.operationId && !['completed', 'stopped'].includes(operation.state));
}

export function mapRecoveryPayload(operation) {
  const graph = operation?.response?.taskGraph;
  if (!graph) throw new Error('Нет подтверждённого результата импорта данных.');
  const scopes = ['ids'];
  if (graph.apply?.connections) scopes.push('connections');
  if (graph.apply?.connectionAccess) scopes.push('connectionAccess');
  if (graph.apply?.layout && graph.layout) scopes.push('layout');
  return {
    schemaVersion: 5, format: 'taskforge-task-graph', scopes, datasets: [],
    courses: (graph.courses || []).map(({ key, id, title }) => ({ key, id, title })),
    tasks: (graph.tasks || []).map(({ key, assignmentId }) => ({ key, id: assignmentId })),
    // Deletions have already committed; this stage may only persist the map.
    connections: graph.connections || [], ...(graph.layout ? { layout: graph.layout } : {}),
  };
}

export function mapRequestFromOperation(operation, extra = {}) {
  const response = operation?.response;
  if (!response?.taskGraph || !operation?.operationId) return null;
  return { key: operation.operationId, operationId: operation.operationId, courseId: operation.courseId,
    taskGraph: response.taskGraph, assignments: response.assignments || [], taskMappings: response.taskMappings || [],
    summary: response, status: 'saving', attempt: 0, ...extra };
}

const auditScopes = new Set(['ids', 'content', 'checks', 'visibility', 'connections', 'connectionAccess', 'layout']);
const booleanOptions = ['updateContent', 'updateChecks', 'updateVisibility', 'updateConnections', 'updateConnectionAccess',
  'updateLayout', 'allowDeletes', 'allowConnectionDeletes', 'allowDuplicates'];
export function safeImportOptions(options = {}) {
  const result = {};
  for (const name of booleanOptions) if (typeof options?.[name] === 'boolean') result[name] = options[name];
  if (['merge', 'replace'].includes(options?.connectionMode)) result.connectionMode = options.connectionMode;
  if (Number.isSafeInteger(options?.expectedMapVersion) && options.expectedMapVersion >= 0) result.expectedMapVersion = options.expectedMapVersion;
  return result;
}
function point(value) {
  return Number.isFinite(value?.x) && Number.isFinite(value?.y) ? { x: value.x, y: value.y } : null;
}
function mapSummary(map) {
  if (!map) return null;
  return {
    version: map.version ?? null, rootCourseId: map.rootCourseId,
    nodes: (map.document?.nodes || []).map(({ id, entityId, type, position }) => ({ id, entityId, type, position: point(position) })),
    edges: (map.document?.edges || []).map(({ id, source, target }) => ({ id, source, target })),
    viewport: map.document?.viewport ? { ...point(map.document.viewport), zoom: map.document.viewport.zoom } : null,
  };
}

// Allowlisted metadata only: never spread raw payloads, assignments, HTTP errors,
// SQL seed/reference scripts, test answers, cookies or authorization headers.
export function buildTaskGraphAudit(operation, diff = null) {
  const response = operation?.response;
  return {
    format: 'taskforge-import-report', version: 1,
    operationId: operation?.operationId || null, courseId: operation?.courseId || null,
    state: operation?.state || 'preview', requestSha256: operation?.requestHash || null,
    createdAt: operation?.createdAt || null, updatedAt: operation?.updatedAt || null,
    rawPayloadIncluded: false, requestHashAlgorithm: 'SHA-256/canonical-json-graph-and-options',
    mapDiff: operation?.audit?.mapDiff ? {
      previousVersion: operation.audit.mapDiff.previousVersion, savedVersion: operation.audit.mapDiff.savedVersion,
      nodes: ['added', 'removed', 'changed'].reduce((r, key) => ({ ...r, [key]: operation.audit.mapDiff.nodes?.[key] || [] }), {}),
      edges: ['added', 'removed', 'changed'].reduce((r, key) => ({ ...r, [key]: operation.audit.mapDiff.edges?.[key] || [] }), {}),
    } : null,
    scopes: (diff?.scopes || operation?.audit?.scopes || []).filter((name) => auditScopes.has(name)),
    options: safeImportOptions(diff?.importOptions || operation?.audit?.options || {}),
    preview: diff ? {
      totals: { operations: diff.total, created: diff.createCount, updated: diff.updateCount,
        deleted: diff.deleteCount, unplaced: diff.unplacedCount },
      tasks: (diff.rows || []).map(({ key, id, action, courseRef, changes }) => ({ key, id, action, courseRef,
        changedFields: (changes || []).map((x) => x.key) })),
      deletedTaskIds: (diff.deleteRows || []).filter((x) => x.action === 'delete').map((x) => x.id),
      connections: (diff.connectionRows || []).map(({ from, to, action }) => ({ from, to, action })),
      removedConnections: (diff.removedConnectionRows || []).map(({ from, to }) => ({ from, to })),
      placements: (diff.placements || []).map(({ key, kind, placed, position }) => ({ key, kind, placed, position })),
      datasets: (diff.datasets || []).map(({ key, action, datasetId }) => ({ key, action, datasetId })),
      issuePaths: (diff.graphIssues || []).map(({ path }) => path),
    } : null,
    applied: response ? {
      created: response.createdCount, updated: response.updatedCount, unchanged: response.unchangedCount, deleted: response.deletedCount,
      entityChanges: (response.entityChanges || []).map(({ assignmentId, action, beforeSha256, afterSha256, changedFields }) =>
        ({ assignmentId, action, beforeSha256, afterSha256, changedFields })),
      createdCourses: response.createdCourseCount, deletedTaskIds: response.deletedAssignmentIds || [],
      tasks: (response.taskGraph?.tasks || []).map(({ key, assignmentId, action, course }) => ({ key, assignmentId, action, course })),
      courses: (response.taskGraph?.courses || []).map(({ key, id }) => ({ key, id })),
    } : null,
    expectedMapVersion: operation?.mapRequest?.expectedVersion ?? null,
    savedMap: mapSummary(operation?.mapResponse),
    error: operation?.error ? { code: /^[A-Z0-9_]{1,80}$/.test(operation.error.code || '') ? operation.error.code : 'IMPORT_UNCONFIRMED' } : null,
  };
}

export function downloadTaskGraphAudit(operation, diff) {
  const report = buildTaskGraphAudit(operation, diff);
  const blob = new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url; link.download = `taskforge-import-${report.operationId || 'preview'}.json`;
  document.body.appendChild(link); link.click(); link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
