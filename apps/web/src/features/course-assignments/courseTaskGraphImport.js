import { assignmentNodeId, assignmentNodeType, courseNodeId } from './courseMapModel';

const COURSE_REF = '$course';

function normalizeEffect(value) {
  const normalized = String(value || 'inherit').trim().toLowerCase();
  return normalized === 'start' || normalized === 'stop' ? normalized : 'inherit';
}

function connectionSettings(connection) {
  const access = connection?.access && typeof connection.access === 'object' ? connection.access : {};
  return {
    hiddenEffect: normalizeEffect(access.hidden),
    sequentialEffect: normalizeEffect(access.sequential),
  };
}

function stableEdgeId(source, target, index) {
  let hash = 2166136261;
  const text = `${source}\u001f${target}\u001f${index}`;
  for (let i = 0; i < text.length; i += 1) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }
  return `edge:task-graph:${(hash >>> 0).toString(36)}:${index}`;
}

function entityIdOf(node) {
  return String(node?.entityId || node?.data?.entityId || '');
}

function isCourseNode(node, courseId) {
  return node?.type === 'course' && entityIdOf(node) === String(courseId || '');
}

function taskRanks(tasks, connections) {
  const keys = tasks.map((task) => String(task?.key || '')).filter(Boolean);
  const order = new Map(keys.map((key, index) => [key, index]));
  const rank = new Map(keys.map((key) => [key, 0]));
  const indegree = new Map(keys.map((key) => [key, 0]));
  const outgoing = new Map(keys.map((key) => [key, []]));

  for (const connection of connections) {
    const from = String(connection?.from || '');
    const to = String(connection?.to || '');
    if (!indegree.has(to)) continue;
    if (from === COURSE_REF) continue;
    if (!outgoing.has(from)) continue;
    outgoing.get(from).push(to);
    indegree.set(to, (indegree.get(to) || 0) + 1);
  }

  const queue = keys
    .filter((key) => (indegree.get(key) || 0) === 0)
    .sort((left, right) => (order.get(left) || 0) - (order.get(right) || 0));
  let cursor = 0;
  while (cursor < queue.length) {
    const source = queue[cursor++];
    for (const target of outgoing.get(source) || []) {
      rank.set(target, Math.max(rank.get(target) || 0, (rank.get(source) || 0) + 1));
      const next = (indegree.get(target) || 0) - 1;
      indegree.set(target, next);
      if (next === 0) queue.push(target);
    }
  }
  return { order, rank };
}

function overlaps(position, occupied) {
  return occupied.some((item) => Math.abs(item.x - position.x) < 270 && Math.abs(item.y - position.y) < 145);
}

function freePosition(preferred, occupied) {
  const offsets = [0];
  for (let step = 1; step <= 80; step += 1) offsets.push(step, -step);
  for (const offset of offsets) {
    const candidate = { x: preferred.x, y: preferred.y + offset * 170 };
    if (!overlaps(candidate, occupied)) return candidate;
  }
  return { x: preferred.x + occupied.length * 24, y: preferred.y + occupied.length * 24 };
}

function layoutNewTaskNodes({ tasks, connections, keyToNodeId, existingNodes, newKeys, anchor }) {
  const { order, rank } = taskRanks(tasks, connections);
  const anchorPosition = {
    x: Number(anchor?.position?.x) || 0,
    y: Number(anchor?.position?.y) || 0,
  };
  const occupied = existingNodes.map((node) => ({
    x: Number(node?.position?.x) || 0,
    y: Number(node?.position?.y) || 0,
  }));
  const layers = new Map();
  for (const key of newKeys) {
    const depth = rank.get(key) || 0;
    if (!layers.has(depth)) layers.set(depth, []);
    layers.get(depth).push(key);
  }
  for (const keys of layers.values()) {
    keys.sort((left, right) => (order.get(left) || 0) - (order.get(right) || 0));
  }

  const result = new Map();
  for (const [depth, keys] of [...layers.entries()].sort((left, right) => left[0] - right[0])) {
    const top = anchorPosition.y - ((keys.length - 1) * 170) / 2;
    keys.forEach((key, index) => {
      const preferred = {
        x: anchorPosition.x + (depth + 1) * 340,
        y: top + index * 170,
      };
      const position = freePosition(preferred, occupied);
      occupied.push(position);
      result.set(keyToNodeId.get(key), position);
    });
  }
  return result;
}

function graphHasCycle(nodes, edges) {
  const ids = new Set(nodes.map((node) => String(node?.id || '')).filter(Boolean));
  const indegree = new Map([...ids].map((id) => [id, 0]));
  const outgoing = new Map([...ids].map((id) => [id, []]));
  for (const edge of edges) {
    const source = String(edge?.source || '');
    const target = String(edge?.target || '');
    if (!ids.has(source) || !ids.has(target) || source === target) return true;
    outgoing.get(source).push(target);
    indegree.set(target, (indegree.get(target) || 0) + 1);
  }
  const queue = [...indegree.entries()].filter(([, value]) => value === 0).map(([id]) => id);
  let cursor = 0;
  while (cursor < queue.length) {
    const id = queue[cursor++];
    for (const target of outgoing.get(id) || []) {
      const next = (indegree.get(target) || 0) - 1;
      indegree.set(target, next);
      if (next === 0) queue.push(target);
    }
  }
  return cursor !== ids.size;
}

function taskGraphMappings(tasks, mappings) {
  const mappingByKey = new Map((Array.isArray(mappings) ? mappings : [])
    .map((item) => [String(item?.key || ''), String(item?.assignmentId || '')])
    .filter(([key, assignmentId]) => key && assignmentId));
  return tasks.map((task) => ({
    ...task,
    assignmentId: String(task?.assignmentId || mappingByKey.get(String(task?.key || '')) || ''),
  }));
}

export function applyTaskGraphImport({ nodes, edges, taskGraph, taskMappings, assignments, courseId }) {
  const rawNodes = Array.isArray(nodes) ? nodes : [];
  const rawEdges = Array.isArray(edges) ? edges : [];
  const sourceTasks = Array.isArray(taskGraph?.tasks) ? taskGraph.tasks : [];
  const tasks = taskGraphMappings(sourceTasks, taskMappings);
  const connections = Array.isArray(taskGraph?.connections) ? taskGraph.connections : [];
  const assignmentById = new Map((Array.isArray(assignments) ? assignments : [])
    .map((assignment) => [String(assignment?.id || ''), assignment])
    .filter(([id]) => id));
  const assignmentIdByKey = new Map(tasks
    .map((task) => [String(task?.key || ''), String(task?.assignmentId || '')])
    .filter(([key, assignmentId]) => key && assignmentId));
  const missingAssignmentIds = [...assignmentIdByKey.values()].filter((id) => !assignmentById.has(id));
  if (missingAssignmentIds.length) return { pending: true, missingAssignmentIds };

  const importedAssignmentIds = new Set(assignmentIdByKey.values());
  const connectedKeys = new Set();
  for (const connection of connections) {
    const from = String(connection?.from || '');
    const to = String(connection?.to || '');
    if (from && from !== COURSE_REF) connectedKeys.add(from);
    if (to) connectedKeys.add(to);
  }
  const connectedAssignmentIds = new Set([...connectedKeys].map((key) => assignmentIdByKey.get(key)).filter(Boolean));
  const detachedAssignmentIds = new Set([...importedAssignmentIds].filter((id) => !connectedAssignmentIds.has(id)));

  const importedNodesByAssignmentId = new Map();
  const allImportedNodeIds = new Set();
  const detachedNodeIds = new Set();
  for (const node of rawNodes) {
    const assignmentId = entityIdOf(node);
    if (!importedAssignmentIds.has(assignmentId)) continue;
    const nodeId = String(node.id);
    allImportedNodeIds.add(nodeId);
    if (detachedAssignmentIds.has(assignmentId)) detachedNodeIds.add(nodeId);
    if (!importedNodesByAssignmentId.has(assignmentId)) importedNodesByAssignmentId.set(assignmentId, node);
  }

  const retainedNodes = rawNodes
    .filter((node) => !allImportedNodeIds.has(String(node.id)))
    .map((node) => ({ ...node, selected: false }));

  let rootNode = retainedNodes.find((node) => isCourseNode(node, courseId))
    || rawNodes.find((node) => isCourseNode(node, courseId));
  const needsRoot = connections.some((connection) => String(connection?.from || '') === COURSE_REF);
  if (needsRoot && !rootNode) {
    const minX = Math.min(0, ...retainedNodes.map((node) => Number(node?.position?.x) || 0));
    rootNode = {
      id: courseNodeId(courseId),
      type: 'course',
      entityId: String(courseId),
      position: { x: minX - 340, y: 0 },
      selected: false,
    };
    retainedNodes.push(rootNode);
  }

  const layoutAnchor = rootNode || {
    position: {
      x: Math.max(0, ...retainedNodes.map((node) => Number(node?.position?.x) || 0)),
      y: 0,
    },
  };

  const keyToNodeId = new Map();
  const importedNodes = [];
  const createdKeys = [];
  const keptImportedNodeIds = new Set();
  for (const task of tasks) {
    const key = String(task?.key || '');
    const assignmentId = assignmentIdByKey.get(key);
    if (!assignmentId || !connectedKeys.has(key)) continue;
    const existing = importedNodesByAssignmentId.get(assignmentId);
    const id = existing ? String(existing.id) : assignmentNodeId(assignmentId);
    keyToNodeId.set(key, id);
    if (existing) {
      keptImportedNodeIds.add(id);
      importedNodes.push({ ...existing, id, entityId: assignmentId, selected: true });
    } else {
      createdKeys.push(key);
    }
  }

  const positions = layoutNewTaskNodes({
    tasks,
    connections,
    keyToNodeId,
    existingNodes: [...retainedNodes, ...importedNodes],
    newKeys: createdKeys,
    anchor: layoutAnchor,
  });
  for (const key of createdKeys) {
    const assignmentId = assignmentIdByKey.get(key);
    const assignment = assignmentById.get(assignmentId);
    importedNodes.push({
      id: keyToNodeId.get(key),
      type: assignmentNodeType(assignment?.type),
      entityId: assignmentId,
      position: positions.get(keyToNodeId.get(key)) || { x: 0, y: 0 },
      selected: true,
    });
  }

  const nextNodes = [...retainedNodes, ...importedNodes];
  const nextNodeIds = new Set(nextNodes.map((node) => String(node.id)));
  const assignmentIdByOldNodeId = new Map(rawNodes
    .map((node) => [String(node?.id || ''), entityIdOf(node)])
    .filter(([, assignmentId]) => importedAssignmentIds.has(assignmentId)));
  const rootNodeId = rootNode ? String(rootNode.id) : '';

  const retainedEdges = rawEdges.filter((edge) => {
    const source = String(edge?.source || '');
    const target = String(edge?.target || '');
    if (!nextNodeIds.has(source) || !nextNodeIds.has(target)) return false;
    if (detachedNodeIds.has(source) || detachedNodeIds.has(target)) return false;
    if (allImportedNodeIds.has(source) && !keptImportedNodeIds.has(source)) return false;
    if (allImportedNodeIds.has(target) && !keptImportedNodeIds.has(target)) return false;

    const sourceAssignmentId = assignmentIdByOldNodeId.get(source);
    const targetAssignmentId = assignmentIdByOldNodeId.get(target);
    if (sourceAssignmentId && targetAssignmentId
      && connectedAssignmentIds.has(sourceAssignmentId)
      && connectedAssignmentIds.has(targetAssignmentId)) return false;
    if (rootNodeId && source === rootNodeId && targetAssignmentId && connectedAssignmentIds.has(targetAssignmentId)) return false;
    return true;
  });

  const importedEdges = [];
  const seenConnections = new Set();
  connections.forEach((connection, index) => {
    const fromKey = String(connection?.from || '');
    const toKey = String(connection?.to || '');
    const source = fromKey === COURSE_REF ? rootNodeId : keyToNodeId.get(fromKey);
    const target = keyToNodeId.get(toKey);
    if (!source || !target || source === target) return;
    const signature = `${source}\u001f${target}`;
    if (seenConnections.has(signature)) return;
    seenConnections.add(signature);
    importedEdges.push({
      id: stableEdgeId(source, target, index),
      source,
      sourceHandle: 'out',
      target,
      targetHandle: 'in',
      settings: connectionSettings(connection),
    });
  });

  const nextEdges = [...retainedEdges, ...importedEdges];
  if (graphHasCycle(nextNodes, nextEdges)) return { error: 'cycle' };

  return {
    nodes: nextNodes,
    edges: nextEdges,
    importedNodeIds: importedNodes.map((node) => String(node.id)),
    createdNodeIds: createdKeys.map((key) => keyToNodeId.get(key)).filter(Boolean),
    unplacedAssignmentIds: [...detachedAssignmentIds],
    detachedCount: detachedAssignmentIds.size,
    connectionCount: importedEdges.length,
  };
}
