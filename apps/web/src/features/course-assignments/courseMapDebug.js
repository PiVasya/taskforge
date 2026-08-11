const PREFIX = '[TFDBG MAP]';
const MAX_IDS = 24;

function compactIds(values) {
  const rows = Array.from(values || []).map((value) => String(value || '')).filter(Boolean);
  if (rows.length <= MAX_IDS) return rows;
  return [...rows.slice(0, MAX_IDS), `...+${rows.length - MAX_IDS}`];
}

export function summarizeCourseMapGraph(nodes = [], edges = []) {
  const listNodes = Array.isArray(nodes) ? nodes : [];
  const listEdges = Array.isArray(edges) ? edges : [];
  const locked = listNodes.filter((node) => node?.type === 'locked' || node?.settings?.synthetic === true || node?.data?.settings?.synthetic === true);
  const syntheticEdges = listEdges.filter((edge) => edge?.settings?.synthetic === true || edge?.data?.settings?.synthetic === true);
  return {
    nodes: listNodes.length,
    edges: listEdges.length,
    lockedNodes: locked.length,
    syntheticEdges: syntheticEdges.length,
    lockedIds: compactIds(locked.map((node) => node?.id)),
    syntheticEdgeIds: compactIds(syntheticEdges.map((edge) => edge?.id)),
  };
}

export function courseMapConsole(event, details = {}, level = 'info') {
  const method = typeof console?.[level] === 'function' ? level : 'info';
  try {
    console[method](PREFIX, event, {
      at: new Date().toISOString(),
      ...details,
    });
  } catch {
    try { console.log(PREFIX, event); } catch {}
  }
}

export function courseMapConsoleGraph(event, { nodes = [], edges = [], ...details } = {}, level = 'info') {
  courseMapConsole(event, {
    ...details,
    ...summarizeCourseMapGraph(nodes, edges),
  }, level);
}

export function hasLearnerSyntheticArtifacts(nodes = [], edges = []) {
  const graph = summarizeCourseMapGraph(nodes, edges);
  return graph.lockedNodes > 0 || graph.syntheticEdges > 0;
}
