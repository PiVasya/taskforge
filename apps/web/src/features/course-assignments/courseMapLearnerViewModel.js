function asId(value) {
  return String(value || '');
}

function isCourseNodeComplete(node) {
  if (node?.type !== 'course') return false;
  const progress = node?.data?.progress || {};
  const total = Math.max(0, Number(progress.total) || 0);
  const solved = Math.max(0, Math.min(total, Number(progress.solved) || 0));
  return total > 0 && solved >= total;
}

function isAssignmentSolved(entity) {
  return entity?.solvedByCurrentUser === true
    || entity?.isSolved === true
    || entity?.completedByCurrentUser === true
    || String(entity?.progressStatus || '').toLowerCase() === 'solved';
}

function nodeSortKey(node) {
  return {
    x: Number(node?.position?.x) || 0,
    y: Number(node?.position?.y) || 0,
    id: asId(node?.id),
  };
}

function compareNodes(left, right) {
  const a = nodeSortKey(left);
  const b = nodeSortKey(right);
  if (a.x !== b.x) return a.x - b.x;
  if (a.y !== b.y) return a.y - b.y;
  return a.id.localeCompare(b.id, 'ru');
}

function buildGraphIndex(nodes, edges) {
  const rows = Array.isArray(nodes) ? nodes.filter(Boolean) : [];
  const links = Array.isArray(edges) ? edges.filter(Boolean) : [];
  const byId = new Map(rows.map((node) => [asId(node.id), node]));
  const outgoing = new Map();
  const incoming = new Map();

  for (const edge of links) {
    const source = asId(edge?.source);
    const target = asId(edge?.target);
    if (!source || !target || !byId.has(source) || !byId.has(target)) continue;
    if (!outgoing.has(source)) outgoing.set(source, []);
    if (!incoming.has(target)) incoming.set(target, []);
    outgoing.get(source).push(edge);
    incoming.get(target).push(edge);
  }

  for (const list of outgoing.values()) {
    list.sort((left, right) => compareNodes(byId.get(asId(left.target)), byId.get(asId(right.target))));
  }

  return { rows, links, byId, outgoing, incoming };
}

function collectCourseSegmentScope(rootId, graph) {
  const scope = new Set();
  const queue = (graph.outgoing.get(rootId) || []).map((edge) => asId(edge.target));

  while (queue.length) {
    const currentId = asId(queue.shift());
    if (!currentId || scope.has(currentId)) continue;
    const node = graph.byId.get(currentId);
    if (!node || node.type === 'course') continue;
    scope.add(currentId);
    for (const edge of graph.outgoing.get(currentId) || []) queue.push(asId(edge.target));
  }

  return scope;
}

function collectCollapsibleSegment(rootId, graph) {
  const scope = collectCourseSegmentScope(rootId, graph);
  const candidate = new Set();
  const visited = new Set();
  const queue = (graph.outgoing.get(rootId) || []).map((edge) => asId(edge.target));
  let unsafeToCollapse = false;

  while (queue.length) {
    const currentId = asId(queue.shift());
    if (!currentId || visited.has(currentId)) continue;
    visited.add(currentId);
    const node = graph.byId.get(currentId);
    if (!node || node.type === 'course') continue;

    if (node.type === 'locked') {
      unsafeToCollapse = true;
      continue;
    }

    const entity = node?.data?.entity || null;
    if (!entity) {
      unsafeToCollapse = true;
      continue;
    }
    if (!isAssignmentSolved(entity)) {
      const hasExternalInput = (graph.incoming.get(currentId) || []).some((edge) => {
        const source = asId(edge.source);
        return source !== rootId && !scope.has(source);
      });
      if (!hasExternalInput) unsafeToCollapse = true;
      continue;
    }

    candidate.add(currentId);
    for (const edge of graph.outgoing.get(currentId) || []) queue.push(asId(edge.target));
  }

  if (unsafeToCollapse || !candidate.size) return new Set();

  const protectedIds = new Set();
  const protectQueue = [];
  for (const candidateId of candidate) {
    const hasExternalInput = (graph.incoming.get(candidateId) || []).some((edge) => {
      const source = asId(edge.source);
      return source !== rootId && !candidate.has(source);
    });
    if (hasExternalInput) {
      protectedIds.add(candidateId);
      protectQueue.push(candidateId);
    }
  }

  while (protectQueue.length) {
    const currentId = asId(protectQueue.shift());
    for (const edge of graph.outgoing.get(currentId) || []) {
      const target = asId(edge.target);
      if (!candidate.has(target) || protectedIds.has(target)) continue;
      protectedIds.add(target);
      protectQueue.push(target);
    }
  }

  return new Set([...candidate].filter((id) => !protectedIds.has(id)));
}

export function buildCompletedCourseLearnerView(nodes, edges, {
  enabled = true,
  expandedCourseIds = [],
} = {}) {
  const graph = buildGraphIndex(nodes, edges);
  if (!enabled) {
    return {
      nodes: graph.rows,
      edges: graph.links,
      collapsedCourseIds: new Set(),
      hiddenNodeIds: new Set(),
      hiddenCountByCourseId: new Map(),
    };
  }

  const expanded = new Set((expandedCourseIds || []).map(asId));
  const hiddenNodeIds = new Set();
  const collapsedCourseIds = new Set();
  const hiddenCountByCourseId = new Map();
  const hiddenByCourse = new Map();

  for (const node of graph.rows) {
    const courseNodeId = asId(node?.id);
    if (!courseNodeId || expanded.has(courseNodeId) || !isCourseNodeComplete(node)) continue;
    const hidden = collectCollapsibleSegment(courseNodeId, graph);
    if (!hidden.size) continue;
    hiddenByCourse.set(courseNodeId, hidden);
    hiddenCountByCourseId.set(courseNodeId, hidden.size);
    collapsedCourseIds.add(courseNodeId);
    for (const id of hidden) hiddenNodeIds.add(id);
  }

  const visibleNodes = graph.rows.filter((node) => !hiddenNodeIds.has(asId(node?.id)));
  const visibleNodeIds = new Set(visibleNodes.map((node) => asId(node.id)));
  const visibleEdges = graph.links.filter((edge) => (
    visibleNodeIds.has(asId(edge?.source)) && visibleNodeIds.has(asId(edge?.target))
  ));
  const directPairs = new Set(visibleEdges.map((edge) => `${asId(edge.source)}>${asId(edge.target)}`));
  const syntheticEdges = [];

  for (const [courseNodeId, localHidden] of hiddenByCourse.entries()) {
    const boundaryTargets = new Set();
    for (const hiddenId of localHidden) {
      for (const edge of graph.outgoing.get(hiddenId) || []) {
        const target = asId(edge.target);
        if (!target || hiddenNodeIds.has(target) || !visibleNodeIds.has(target)) continue;
        boundaryTargets.add(target);
      }
    }

    for (const target of boundaryTargets) {
      const pair = `${courseNodeId}>${target}`;
      if (directPairs.has(pair)) continue;
      directPairs.add(pair);
      syntheticEdges.push({
        id: `learner-collapse:${courseNodeId}:${target}`,
        source: courseNodeId,
        target,
        sourceHandle: 'out',
        targetHandle: 'in',
        type: 'courseMap',
        className: 'course-map-edge is-learner-collapsed',
        interactionWidth: 14,
        data: { learnerCollapsed: true },
      });
    }
  }

  return {
    nodes: visibleNodes,
    edges: [...visibleEdges, ...syntheticEdges],
    collapsedCourseIds,
    hiddenNodeIds,
    hiddenCountByCourseId,
  };
}

export function findCourseLearnerAssignmentAction(courseNodeId, nodes, edges) {
  const rootId = asId(courseNodeId);
  if (!rootId) return { assignmentId: '', hasUnsolved: false };
  const graph = buildGraphIndex(nodes, edges);
  if (!graph.byId.has(rootId)) return { assignmentId: '', hasUnsolved: false };

  const queue = (graph.outgoing.get(rootId) || []).map((edge) => asId(edge.target));
  const visited = new Set();
  let lastAssignmentId = '';

  while (queue.length) {
    const currentId = asId(queue.shift());
    if (!currentId || visited.has(currentId)) continue;
    visited.add(currentId);
    const node = graph.byId.get(currentId);
    if (!node) continue;

    if (node.type === 'locked') continue;

    if (node.type !== 'course') {
      const entity = node?.data?.entity || null;
      const assignmentId = asId(node?.entityId || node?.data?.entityId || entity?.id);
      if (assignmentId) {
        lastAssignmentId = assignmentId;
        if (entity && !isAssignmentSolved(entity)) {
          return { assignmentId, hasUnsolved: true };
        }
      }
    }

    for (const edge of graph.outgoing.get(currentId) || []) queue.push(asId(edge.target));
  }

  return { assignmentId: lastAssignmentId, hasUnsolved: false };
}
