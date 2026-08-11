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

function layoutPosition(taskGraph, ref) {
  const value = taskGraph?.layout?.positions?.[ref];
  if (!value || !Number.isFinite(Number(value.x)) || !Number.isFinite(Number(value.y))) return null;
  return { x: Number(value.x), y: Number(value.y) };
}

function graphApplyOptions(taskGraph) {
  const apply = taskGraph?.apply && typeof taskGraph.apply === 'object' ? taskGraph.apply : {};
  return {
    connections: apply.connections !== false,
    connectionAccess: apply.connectionAccess !== false,
    layout: apply.layout !== false,
  };
}

function preserveEdgeSettings(rawEdges, source, target) {
  const existing = rawEdges.find((edge) => String(edge?.source || '') === source && String(edge?.target || '') === target);
  return existing?.settings ? { ...existing.settings } : { hiddenEffect: 'inherit', sequentialEffect: 'inherit' };
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

function overlaps(position, occupied) {
  return occupied.some((item) => Math.abs(item.x - position.x) < 270 && Math.abs(item.y - position.y) < 145);
}

function freePosition(preferred, occupied) {
  const offsets = [0];
  for (let step = 1; step <= 100; step += 1) offsets.push(step, -step);
  for (const offset of offsets) {
    const candidate = { x: preferred.x, y: preferred.y + offset * 170 };
    if (!overlaps(candidate, occupied)) return candidate;
  }
  return { x: preferred.x + occupied.length * 24, y: preferred.y + occupied.length * 24 };
}

function graphRanks(refs, connections) {
  const rows = [...refs].filter((ref) => ref && ref !== COURSE_REF);
  const order = new Map(rows.map((ref, index) => [ref, index]));
  const rank = new Map(rows.map((ref) => [ref, 0]));
  const indegree = new Map(rows.map((ref) => [ref, 0]));
  const outgoing = new Map(rows.map((ref) => [ref, []]));
  for (const connection of connections || []) {
    const from = String(connection?.from || '');
    const to = String(connection?.to || '');
    if (!indegree.has(to)) continue;
    if (from === COURSE_REF) continue;
    if (!outgoing.has(from)) continue;
    outgoing.get(from).push(to);
    indegree.set(to, (indegree.get(to) || 0) + 1);
  }
  const queue = rows
    .filter((ref) => (indegree.get(ref) || 0) === 0)
    .sort((a, b) => (order.get(a) || 0) - (order.get(b) || 0));
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
  return { rank, order };
}

function autoPositions({ refs, allRefs, connections, descriptors, existingNodes, anchor }) {
  const { rank, order } = graphRanks(allRefs || refs, connections);
  const occupied = existingNodes.map((node) => ({
    x: Number(node?.position?.x) || 0,
    y: Number(node?.position?.y) || 0,
  }));
  const anchorPosition = {
    x: Number(anchor?.position?.x) || 0,
    y: Number(anchor?.position?.y) || 0,
  };
  const layers = new Map();
  for (const ref of refs) {
    if (ref === COURSE_REF) continue;
    const depth = rank.get(ref) || 0;
    if (!layers.has(depth)) layers.set(depth, []);
    layers.get(depth).push(ref);
  }
  const result = new Map();
  for (const [depth, layerRefs] of [...layers.entries()].sort((a, b) => a[0] - b[0])) {
    layerRefs.sort((a, b) => (order.get(a) || 0) - (order.get(b) || 0));
    const top = anchorPosition.y - ((layerRefs.length - 1) * 170) / 2;
    layerRefs.forEach((ref, index) => {
      const descriptor = descriptors.get(ref);
      if (!descriptor) return;
      const preferred = { x: anchorPosition.x + (depth + 1) * 340, y: top + index * 170 };
      const position = freePosition(preferred, occupied);
      occupied.push(position);
      result.set(ref, position);
    });
  }
  return result;
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

function buildDescriptors(taskGraph, taskMappings, courseId, assignments) {
  const assignmentById = new Map((Array.isArray(assignments) ? assignments : [])
    .map((assignment) => [String(assignment?.id || ''), assignment])
    .filter(([id]) => id));
  const courses = Array.isArray(taskGraph?.courses) ? taskGraph.courses : [];
  const tasks = taskGraphMappings(Array.isArray(taskGraph?.tasks) ? taskGraph.tasks : [], taskMappings);
  const descriptors = new Map();
  descriptors.set(COURSE_REF, {
    ref: COURSE_REF,
    kind: 'course',
    entityId: String(courseId || ''),
    nodeId: courseNodeId(courseId),
  });
  for (const course of courses) {
    const ref = String(course?.key || '').trim();
    const entityId = String(course?.id || '').trim();
    if (!ref || !entityId) continue;
    descriptors.set(ref, { ref, kind: 'course', entityId, nodeId: courseNodeId(entityId), course });
  }
  for (const task of tasks) {
    const ref = String(task?.key || '').trim();
    const entityId = String(task?.assignmentId || '').trim();
    if (!ref || !entityId) continue;
    descriptors.set(ref, {
      ref,
      kind: 'assignment',
      entityId,
      nodeId: assignmentNodeId(entityId),
      task,
      assignment: assignmentById.get(entityId),
    });
  }
  return { descriptors, tasks, assignmentById };
}

function nodeForDescriptor(descriptor, position, existing = null) {
  if (descriptor.kind === 'course') {
    return {
      ...(existing || {}),
      id: existing?.id || descriptor.nodeId,
      type: 'course',
      entityId: descriptor.entityId,
      position,
      selected: descriptor.ref !== COURSE_REF,
    };
  }
  return {
    ...(existing || {}),
    id: existing?.id || descriptor.nodeId,
    type: assignmentNodeType(descriptor.assignment?.type || descriptor.task?.type),
    entityId: descriptor.entityId,
    position,
    selected: true,
  };
}

export function applyTaskGraphImport({ nodes, edges, taskGraph, taskMappings, assignments, courseId }) {
  const rawNodes = Array.isArray(nodes) ? nodes : [];
  const rawEdges = Array.isArray(edges) ? edges : [];
  const connections = Array.isArray(taskGraph?.connections) ? taskGraph.connections : [];
  const apply = graphApplyOptions(taskGraph);
  const { descriptors, tasks, assignmentById } = buildDescriptors(taskGraph, taskMappings, courseId, assignments);

  const missingAssignmentIds = tasks
    .map((task) => String(task?.assignmentId || ''))
    .filter((id) => id && !assignmentById.has(id));
  if (missingAssignmentIds.length) return { pending: true, missingAssignmentIds };

  const descriptorByEntity = new Map([...descriptors.values()].map((item) => [`${item.kind}:${item.entityId}`, item]));
  const refByNodeId = new Map();
  const existingNodeByRef = new Map();
  for (const node of rawNodes) {
    const kind = node?.type === 'course' ? 'course' : 'assignment';
    const descriptor = descriptorByEntity.get(`${kind}:${entityIdOf(node)}`);
    if (!descriptor) continue;
    refByNodeId.set(String(node.id), descriptor.ref);
    if (!existingNodeByRef.has(descriptor.ref)) existingNodeByRef.set(descriptor.ref, node);
  }

  const nodeIdByRef = new Map();
  for (const [ref, descriptor] of descriptors) {
    nodeIdByRef.set(ref, String(existingNodeByRef.get(ref)?.id || descriptor.nodeId));
  }

  if (!apply.connections) {
    let nextNodes = rawNodes.map((node) => ({ ...node, selected: false }));
    const importedNodeIds = [];
    const createdNodeIds = [];
    if (apply.layout) {
      for (const [ref, descriptor] of descriptors) {
        const position = layoutPosition(taskGraph, ref);
        if (!position) continue;
        const existing = existingNodeByRef.get(ref);
        if (existing) {
          nextNodes = nextNodes.map((node) => String(node.id) === String(existing.id)
            ? { ...node, position, selected: ref !== COURSE_REF }
            : node);
          if (ref !== COURSE_REF) importedNodeIds.push(String(existing.id));
        } else {
          const created = nodeForDescriptor(descriptor, position);
          nextNodes.push(created);
          createdNodeIds.push(String(created.id));
          if (ref !== COURSE_REF) importedNodeIds.push(String(created.id));
          nodeIdByRef.set(ref, String(created.id));
        }
      }
    }

    let nextEdges = rawEdges.map((edge) => ({ ...edge }));
    if (apply.connectionAccess) {
      const settingsByPair = new Map();
      for (const connection of connections) {
        const source = nodeIdByRef.get(String(connection?.from || ''));
        const target = nodeIdByRef.get(String(connection?.to || ''));
        if (source && target) settingsByPair.set(`${source}\u001f${target}`, connectionSettings(connection));
      }
      nextEdges = nextEdges.map((edge) => {
        const settings = settingsByPair.get(`${String(edge?.source || '')}\u001f${String(edge?.target || '')}`);
        return settings ? { ...edge, settings } : edge;
      });
    }

    return {
      nodes: nextNodes,
      edges: nextEdges,
      importedNodeIds,
      createdNodeIds,
      unplacedAssignmentIds: [],
      detachedCount: 0,
      connectionCount: 0,
      viewport: apply.layout && taskGraph?.layout?.viewport ? { ...taskGraph.layout.viewport } : null,
    };
  }

  const connectedRefs = new Set();
  for (const connection of connections) {
    const from = String(connection?.from || '');
    const to = String(connection?.to || '');
    if (from) connectedRefs.add(from);
    if (to) connectedRefs.add(to);
  }
  const layoutRefs = new Set(Object.keys(taskGraph?.layout?.positions || {}));
  const shouldPlace = new Set([COURSE_REF, ...connectedRefs, ...(apply.layout ? layoutRefs : [])]);
  const importedExistingNodeIds = new Set();
  const removedImportedNodeIds = new Set();
  for (const [ref, existing] of existingNodeByRef) {
    if (ref === COURSE_REF || shouldPlace.has(ref)) importedExistingNodeIds.add(String(existing.id));
    else removedImportedNodeIds.add(String(existing.id));
  }

  const retainedNodes = rawNodes
    .filter((node) => !removedImportedNodeIds.has(String(node.id)))
    .map((node) => ({ ...node, selected: false }));
  const retainedNodeIds = new Set(retainedNodes.map((node) => String(node.id)));

  let rootNode = retainedNodes.find((node) => String(node.id) === nodeIdByRef.get(COURSE_REF));
  if (!rootNode) {
    const descriptor = descriptors.get(COURSE_REF);
    rootNode = nodeForDescriptor(descriptor, layoutPosition(taskGraph, COURSE_REF) || { x: 0, y: 0 });
    rootNode.selected = false;
    retainedNodes.push(rootNode);
    retainedNodeIds.add(String(rootNode.id));
    nodeIdByRef.set(COURSE_REF, String(rootNode.id));
  }

  const refsNeedingNewNode = [...shouldPlace].filter((ref) => ref !== COURSE_REF && descriptors.has(ref) && !existingNodeByRef.has(ref));
  const auto = autoPositions({
    refs: refsNeedingNewNode,
    allRefs: [...descriptors.keys()],
    connections,
    descriptors,
    existingNodes: retainedNodes,
    anchor: rootNode,
  });

  let nextNodes = retainedNodes;
  const importedNodeIds = [];
  const createdNodeIds = [];
  for (const [ref, descriptor] of descriptors) {
    if (!shouldPlace.has(ref)) continue;
    const explicitPosition = apply.layout ? layoutPosition(taskGraph, ref) : null;
    const existing = nextNodes.find((node) => String(node.id) === nodeIdByRef.get(ref));
    if (existing) {
      if (explicitPosition) {
        nextNodes = nextNodes.map((node) => String(node.id) === String(existing.id)
          ? { ...node, position: explicitPosition, selected: ref !== COURSE_REF }
          : node);
      }
      if (ref !== COURSE_REF) importedNodeIds.push(String(existing.id));
      continue;
    }
    const position = explicitPosition || auto.get(ref) || freePosition(rootNode.position || { x: 0, y: 0 }, nextNodes.map((node) => node.position || { x: 0, y: 0 }));
    const created = nodeForDescriptor(descriptor, position);
    nextNodes.push(created);
    nodeIdByRef.set(ref, String(created.id));
    createdNodeIds.push(String(created.id));
    importedNodeIds.push(String(created.id));
  }

  const importedNodeIdSet = new Set([...descriptors.keys()].map((ref) => nodeIdByRef.get(ref)).filter(Boolean));
  const nextNodeIds = new Set(nextNodes.map((node) => String(node.id)));
  const retainedEdges = rawEdges.filter((edge) => {
    const source = String(edge?.source || '');
    const target = String(edge?.target || '');
    if (!nextNodeIds.has(source) || !nextNodeIds.has(target)) return false;
    const bothImported = importedNodeIdSet.has(source) && importedNodeIdSet.has(target);
    return !bothImported;
  });

  const importedEdges = [];
  const seen = new Set();
  connections.forEach((connection, index) => {
    const source = nodeIdByRef.get(String(connection?.from || ''));
    const target = nodeIdByRef.get(String(connection?.to || ''));
    if (!source || !target || source === target || !nextNodeIds.has(source) || !nextNodeIds.has(target)) return;
    const signature = `${source}\u001f${target}`;
    if (seen.has(signature)) return;
    seen.add(signature);
    importedEdges.push({
      id: stableEdgeId(source, target, index),
      source,
      sourceHandle: 'out',
      target,
      targetHandle: 'in',
      settings: apply.connectionAccess ? connectionSettings(connection) : preserveEdgeSettings(rawEdges, source, target),
    });
  });

  const nextEdges = [...retainedEdges, ...importedEdges];
  if (graphHasCycle(nextNodes, nextEdges)) return { error: 'cycle' };

  const unplacedAssignmentIds = [...descriptors.values()]
    .filter((descriptor) => descriptor.kind === 'assignment' && !shouldPlace.has(descriptor.ref))
    .map((descriptor) => descriptor.entityId);

  return {
    nodes: nextNodes,
    edges: nextEdges,
    importedNodeIds,
    createdNodeIds,
    unplacedAssignmentIds,
    detachedCount: unplacedAssignmentIds.length,
    connectionCount: importedEdges.length,
    viewport: apply.layout && taskGraph?.layout?.viewport ? { ...taskGraph.layout.viewport } : null,
  };
}
