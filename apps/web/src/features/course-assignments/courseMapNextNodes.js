function clean(value) {
  return String(value || '').trim();
}

function assignmentTitle(item) {
  return clean(item?.title) || 'Следующее задание';
}

function nodeOrder(node) {
  return {
    y: Number(node?.position?.y) || 0,
    x: Number(node?.position?.x) || 0,
    id: clean(node?.id),
  };
}

function compareNodeIds(leftId, rightId, byId) {
  const left = nodeOrder(byId.get(leftId));
  const right = nodeOrder(byId.get(rightId));
  if (left.y !== right.y) return left.y - right.y;
  if (left.x !== right.x) return left.x - right.x;
  return left.id.localeCompare(right.id, 'ru');
}

export function courseMapContainsAssignment(document, assignmentId) {
  const currentEntityId = clean(assignmentId);
  if (!currentEntityId) return false;
  return (Array.isArray(document?.nodes) ? document.nodes : []).some((node) => {
    const type = clean(node?.type).toLowerCase();
    return clean(node?.entityId) === currentEntityId && type !== 'course' && type !== 'locked';
  });
}

export function buildNextNodeOptions(document, assignmentId, assignments = []) {
  const nodes = Array.isArray(document?.nodes) ? document.nodes : [];
  const edges = Array.isArray(document?.edges) ? document.edges : [];
  const currentEntityId = clean(assignmentId);
  if (!currentEntityId || !nodes.length) return [];

  const byId = new Map(nodes.map((node) => [clean(node?.id), node]).filter(([id]) => id));
  const assignmentById = new Map((Array.isArray(assignments) ? assignments : []).map((item) => [clean(item?.id), item]));
  const currentNodes = nodes.filter((node) => {
    const type = clean(node?.type).toLowerCase();
    return clean(node?.entityId) === currentEntityId && type !== 'course' && type !== 'locked';
  });
  if (!currentNodes.length) return [];

  const outgoing = new Map();
  for (const edge of edges) {
    const source = clean(edge?.source);
    const target = clean(edge?.target);
    if (!source || !target || !byId.has(source) || !byId.has(target)) continue;
    if (!outgoing.has(source)) outgoing.set(source, []);
    outgoing.get(source).push(target);
  }
  for (const targets of outgoing.values()) targets.sort((left, right) => compareNodeIds(left, right, byId));

  const result = [];
  const seenResultKeys = new Set();
  const seenTraversal = new Set();
  const pending = currentNodes
    .flatMap((node) => outgoing.get(clean(node.id)) || [])
    .sort((left, right) => compareNodeIds(left, right, byId));

  while (pending.length) {
    const nodeId = clean(pending.shift());
    if (!nodeId || seenTraversal.has(nodeId)) continue;
    seenTraversal.add(nodeId);
    const node = byId.get(nodeId);
    if (!node) continue;
    const type = clean(node.type).toLowerCase();

    if (type === 'course') {
      pending.push(...(outgoing.get(nodeId) || []));
      continue;
    }

    if (type === 'locked') {
      const resultKey = `locked:${nodeId}`;
      if (seenResultKeys.has(resultKey)) continue;
      seenResultKeys.add(resultKey);
      result.push({
        key: resultKey,
        nodeId,
        kind: 'locked',
        disabled: true,
        title: clean(node?.settings?.title) || 'Продолжение закрыто',
        subtitle: clean(node?.settings?.requirement) || 'Выполните условие, чтобы открыть продолжение.',
        order: nodeOrder(node),
      });
      continue;
    }

    const entityId = clean(node.entityId);
    if (!entityId || entityId === currentEntityId) continue;
    const resultKey = `assignment:${entityId}`;
    if (seenResultKeys.has(resultKey)) continue;
    seenResultKeys.add(resultKey);
    const assignment = assignmentById.get(entityId);
    result.push({
      key: resultKey,
      nodeId,
      kind: 'assignment',
      id: entityId,
      title: assignmentTitle(assignment),
      subtitle: clean(assignment?.tags) || clean(assignment?.type || type),
      disabled: false,
      order: nodeOrder(node),
    });
  }

  return result
    .sort((left, right) => {
      if (left.order.y !== right.order.y) return left.order.y - right.order.y;
      if (left.order.x !== right.order.x) return left.order.x - right.order.x;
      return left.title.localeCompare(right.title, 'ru');
    })
    .map(({ order, ...item }) => item);
}

export function buildSortedFallbackNext(assignments, assignmentId) {
  const ordered = (Array.isArray(assignments) ? assignments : []).slice().sort((left, right) => {
    const sortDiff = Number(left?.sort || 0) - Number(right?.sort || 0);
    if (sortDiff) return sortDiff;
    return assignmentTitle(left).localeCompare(assignmentTitle(right), 'ru');
  });
  const index = ordered.findIndex((item) => clean(item?.id) === clean(assignmentId));
  const next = index >= 0 ? ordered[index + 1] : null;
  return next?.id ? [{
    key: `assignment:${clean(next.id)}`,
    kind: 'assignment',
    id: clean(next.id),
    title: assignmentTitle(next),
    subtitle: clean(next?.tags) || clean(next?.type),
    disabled: false,
  }] : [];
}
