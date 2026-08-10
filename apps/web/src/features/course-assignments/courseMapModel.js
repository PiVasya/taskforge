import { isAssignmentSolved } from './courseAssignmentsModel';

export const COURSE_MAP_SCHEMA_VERSION = 1;
export const COURSE_MAP_NODE_TYPES = Object.freeze(['course', 'code-test', 'test', 'image-code', 'math', 'locked']);

export function assignmentNodeType(type) {
  const normalized = String(type || '').trim().toLowerCase();
  if (normalized === 'test') return 'test';
  if (normalized === 'image-test' || normalized === 'image-code') return 'image-code';
  if (normalized === 'math') return 'math';
  return 'code-test';
}

export function courseNodeId(courseId) {
  return `course:${String(courseId)}`;
}

export function assignmentNodeId(assignmentId) {
  return `assignment:${String(assignmentId)}`;
}

function sortValue(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number : 0;
}

function normalizeCourseList(rootCourseId, courses) {
  const rows = Array.isArray(courses) ? courses.filter(Boolean) : [];
  const byId = new Map(rows.map((item) => [String(item.id), item]));
  const root = byId.get(String(rootCourseId));
  if (!root) return [];
  const children = new Map();
  for (const row of rows) {
    const parent = String(row.parentCourseId || '');
    if (!children.has(parent)) children.set(parent, []);
    children.get(parent).push(row);
  }
  for (const list of children.values()) {
    list.sort((a, b) => sortValue(a.sort) - sortValue(b.sort) || String(a.title || '').localeCompare(String(b.title || ''), 'ru'));
  }
  const result = [];
  const seen = new Set();
  const walk = (id) => {
    const key = String(id);
    if (seen.has(key)) return;
    seen.add(key);
    const row = byId.get(key);
    if (!row) return;
    result.push(row);
    for (const child of children.get(key) || []) walk(child.id);
  };
  walk(rootCourseId);
  return result;
}

export function resolveRootCourseId(courseId, courses) {
  const rows = Array.isArray(courses) ? courses : [];
  const byId = new Map(rows.map((item) => [String(item?.id || ''), item]));
  let current = String(courseId || '');
  if (!current) return current;
  const seen = new Set();
  while (current && !seen.has(current)) {
    seen.add(current);
    const row = byId.get(current);
    const parent = String(row?.parentCourseId || '');
    if (!parent || !byId.has(parent)) return current;
    current = parent;
  }
  return String(courseId || '');
}

function normalizeMapEffect(value, legacyStart = false) {
  const normalized = String(value || '').trim().toLowerCase();
  if (normalized === 'start' || normalized === 'stop' || normalized === 'inherit') return normalized;
  return legacyStart ? 'start' : 'inherit';
}

function mapEdgeEffects(edge) {
  const settings = edge?.settings && typeof edge.settings === 'object' ? edge.settings : {};
  const legacyMode = String(settings.accessMode || 'normal').trim().toLowerCase();
  return {
    hidden: normalizeMapEffect(
      settings.hiddenEffect,
      settings.gateUntilPrerequisites === true || legacyMode === 'after-prerequisites',
    ),
    sequential: normalizeMapEffect(
      settings.sequentialEffect,
      settings.sequentialReveal === true || legacyMode === 'sequential',
    ),
  };
}

function applyMapEffect(current, transition) {
  if (transition === 'start') return true;
  if (transition === 'stop') return false;
  return current;
}

export function computeCourseMapAccessEffects(nodes, edges) {
  const mapNodes = Array.isArray(nodes) ? nodes : [];
  const mapEdges = Array.isArray(edges) ? edges : [];
  const nodeIds = new Set(mapNodes.map((node) => String(node?.id || '')).filter(Boolean));
  const incomingCount = new Map([...nodeIds].map((id) => [id, 0]));
  const outgoing = new Map([...nodeIds].map((id) => [id, []]));

  for (const edge of mapEdges) {
    const source = String(edge?.source || '');
    const target = String(edge?.target || '');
    if (!nodeIds.has(source) || !nodeIds.has(target) || source === target) continue;
    outgoing.get(source).push(edge);
    incomingCount.set(target, (incomingCount.get(target) || 0) + 1);
  }

  const roots = [...nodeIds].filter((id) => (incomingCount.get(id) || 0) === 0);
  if (!roots.length && mapNodes[0]?.id) roots.push(String(mapNodes[0].id));

  const queue = roots.map((id) => ({ id, hidden: false, sequential: false }));
  let queueIndex = 0;
  const seen = new Set();
  const rawStates = new Map();

  while (queueIndex < queue.length) {
    const state = queue[queueIndex++];
    if (!state || !nodeIds.has(state.id)) continue;
    const key = `${state.id}\u001f${state.hidden ? 1 : 0}\u001f${state.sequential ? 1 : 0}`;
    if (seen.has(key)) continue;
    seen.add(key);

    if (!rawStates.has(state.id)) rawStates.set(state.id, []);
    rawStates.get(state.id).push({ hidden: state.hidden, sequential: state.sequential });

    for (const edge of outgoing.get(state.id) || []) {
      const effects = mapEdgeEffects(edge);
      queue.push({
        id: String(edge.target),
        hidden: applyMapEffect(state.hidden, effects.hidden),
        sequential: applyMapEffect(state.sequential, effects.sequential),
      });
    }
  }

  // Disconnected fragments without an incoming-free root are invalid for progression,
  // but the editor should still render them deterministically instead of dropping all
  // decoration. Treat any unseen node as a normal standalone fragment.
  for (const id of nodeIds) {
    if (!rawStates.has(id)) rawStates.set(id, [{ hidden: false, sequential: false }]);
  }

  const result = new Map();
  for (const [id, states] of rawStates) {
    const hidden = states.some((state) => state.hidden);
    const visible = states.some((state) => !state.hidden);
    const sequential = states.some((state) => state.sequential);
    const nonSequential = states.some((state) => !state.sequential);
    result.set(id, {
      hidden,
      sequential,
      hiddenMixed: hidden && visible,
      sequentialMixed: sequential && nonSequential,
      combined: states.some((state) => state.hidden && state.sequential),
    });
  }
  return result;
}

function snakePosition(index) {
  const columns = 5;
  const column = index % columns;
  const row = Math.floor(index / columns);
  const actualColumn = row % 2 === 0 ? column : columns - 1 - column;
  return {
    x: actualColumn * 330,
    y: row * 190,
  };
}

function makeEdge(source, target, index) {
  return {
    id: `edge:${index}:${source}:${target}`,
    source,
    sourceHandle: 'out',
    target,
    targetHandle: 'in',
    settings: { hiddenEffect: 'inherit', sequentialEffect: 'inherit' },
  };
}

export function buildDefaultCourseMap(rootCourseId, courses, assignments) {
  const subtreeCourses = normalizeCourseList(rootCourseId, courses);
  const courseById = new Map(subtreeCourses.map((item) => [String(item.id), item]));
  const assignmentsByCourse = new Map();
  for (const item of Array.isArray(assignments) ? assignments : []) {
    const id = String(item?.courseId || '');
    if (!courseById.has(id)) continue;
    if (!assignmentsByCourse.has(id)) assignmentsByCourse.set(id, []);
    assignmentsByCourse.get(id).push(item);
  }
  for (const list of assignmentsByCourse.values()) {
    list.sort((a, b) => sortValue(a.sort) - sortValue(b.sort) || String(a.title || '').localeCompare(String(b.title || ''), 'ru'));
  }

  const childCoursesByParent = new Map();
  for (const item of subtreeCourses) {
    const parent = String(item.parentCourseId || '');
    if (!childCoursesByParent.has(parent)) childCoursesByParent.set(parent, []);
    childCoursesByParent.get(parent).push(item);
  }
  for (const list of childCoursesByParent.values()) {
    list.sort((a, b) => sortValue(a.sort) - sortValue(b.sort) || String(a.title || '').localeCompare(String(b.title || ''), 'ru'));
  }

  const nodes = [];
  const edges = [];
  const seen = new Set();
  let edgeIndex = 0;

  const pushNode = (node) => {
    if (seen.has(node.id)) return;
    seen.add(node.id);
    nodes.push({ ...node, position: snakePosition(nodes.length) });
  };

  const walkCourse = (courseId, guard = new Set()) => {
    const key = String(courseId);
    if (guard.has(key)) return { head: null, tails: [] };
    const nextGuard = new Set(guard);
    nextGuard.add(key);
    const course = courseById.get(key);
    if (!course) return { head: null, tails: [] };

    const head = courseNodeId(course.id);
    pushNode({ id: head, type: 'course', entityId: String(course.id) });
    let tails = [head];

    const content = [
      ...(assignmentsByCourse.get(key) || []).map((item) => ({ kind: 'assignment', sort: sortValue(item.sort), item })),
      ...(childCoursesByParent.get(key) || []).map((item) => ({ kind: 'course', sort: sortValue(item.sort), item })),
    ].sort((a, b) => a.sort - b.sort || (a.kind === b.kind ? 0 : a.kind === 'course' ? -1 : 1));

    for (const entry of content) {
      if (entry.kind === 'assignment') {
        const nodeId = assignmentNodeId(entry.item.id);
        pushNode({ id: nodeId, type: assignmentNodeType(entry.item.type), entityId: String(entry.item.id) });
        for (const tail of tails) edges.push(makeEdge(tail, nodeId, edgeIndex++));
        tails = [nodeId];
      } else {
        const child = walkCourse(entry.item.id, nextGuard);
        if (!child.head) continue;
        for (const tail of tails) edges.push(makeEdge(tail, child.head, edgeIndex++));
        tails = child.tails.length ? child.tails : [child.head];
      }
    }

    return { head, tails };
  };

  walkCourse(rootCourseId);

  return {
    schemaVersion: COURSE_MAP_SCHEMA_VERSION,
    viewport: { x: 90, y: 70, zoom: 0.88 },
    nodes,
    edges,
  };
}

export function buildEntityIndex(courses, assignments) {
  const courseById = new Map((Array.isArray(courses) ? courses : []).filter(Boolean).map((item) => [String(item.id), item]));
  const assignmentById = new Map((Array.isArray(assignments) ? assignments : []).filter(Boolean).map((item) => [String(item.id), item]));
  return { courseById, assignmentById };
}

export function readCourseProgress(document) {
  if (!document || Number(document.courseProgressVersion) !== 1) return new Map();
  const source = document.courseProgress;
  if (!source || typeof source !== 'object' || Array.isArray(source)) return new Map();
  const result = new Map();
  for (const [nodeId, raw] of Object.entries(source)) {
    if (!nodeId || !raw || typeof raw !== 'object') continue;
    const total = Math.max(0, Number(raw.total) || 0);
    const solved = Math.max(0, Math.min(total, Number(raw.solved) || 0));
    const percent = total > 0
      ? Math.max(0, Math.min(100, Number.isFinite(Number(raw.percent)) ? Math.round(Number(raw.percent)) : Math.round((solved / total) * 100)))
      : 0;
    result.set(String(nodeId), { total, solved, percent });
  }
  return result;
}

export function writeCourseProgress(progressByNode) {
  const result = {};
  for (const [nodeId, raw] of progressByNode instanceof Map ? progressByNode.entries() : []) {
    if (!nodeId || !raw) continue;
    const total = Math.max(0, Number(raw.total) || 0);
    const solved = Math.max(0, Math.min(total, Number(raw.solved) || 0));
    result[String(nodeId)] = {
      total,
      solved,
      percent: total > 0 ? Math.round((solved / total) * 100) : 0,
    };
  }
  return result;
}

export function computeCourseProgress(nodes, edges, assignments, rootCourseId) {
  const mapNodes = Array.isArray(nodes) ? nodes : [];
  const mapEdges = Array.isArray(edges) ? edges : [];
  const byNodeId = new Map(mapNodes.map((node) => [String(node?.id || ''), node]));
  const assignmentsById = new Map((Array.isArray(assignments) ? assignments : []).map((item) => [String(item?.id || ''), item]));
  const outgoing = new Map();
  for (const edge of mapEdges) {
    const source = String(edge?.source || '');
    const target = String(edge?.target || '');
    if (!source || !target) continue;
    if (!outgoing.has(source)) outgoing.set(source, []);
    outgoing.get(source).push(target);
  }

  const result = new Map();
  for (const courseNode of mapNodes.filter((node) => node?.type === 'course')) {
    const courseId = String(courseNode?.entityId || courseNode?.data?.entityId || '');
    const includeNestedCourseSegments = courseId && courseId === String(rootCourseId || '');
    const visited = new Set();
    const assignmentIds = new Set();
    const pending = [...(outgoing.get(String(courseNode.id)) || [])];

    while (pending.length) {
      const currentId = String(pending.pop() || '');
      if (!currentId || visited.has(currentId)) continue;
      visited.add(currentId);
      const current = byNodeId.get(currentId);
      if (!current) continue;

      // A child Course node is a semantic boundary: its own progress starts
      // there. The root Course is the only one that intentionally rolls up
      // the complete reachable map, including nested course segments.
      if (current.type === 'course' && !includeNestedCourseSegments) continue;

      if (current.type !== 'course') {
        const entityId = String(current?.entityId || current?.data?.entityId || '');
        if (entityId) assignmentIds.add(entityId);
      }
      for (const nextId of outgoing.get(currentId) || []) pending.push(nextId);
    }

    const resolvedAssignments = [...assignmentIds].map((id) => assignmentsById.get(id)).filter(Boolean);
    const total = resolvedAssignments.length;
    const solved = resolvedAssignments.filter(isAssignmentSolved).length;
    result.set(String(courseNode.id), {
      total,
      solved,
      percent: total > 0 ? Math.round((solved / total) * 100) : 0,
    });
  }
  return result;
}

export function normalizeStoredMap(document, courses, assignments) {
  if (!document || Number(document.schemaVersion) !== COURSE_MAP_SCHEMA_VERSION) return null;
  const { courseById, assignmentById } = buildEntityIndex(courses, assignments);
  const nodes = [];
  const validIds = new Set();
  for (const raw of Array.isArray(document.nodes) ? document.nodes : []) {
    if (!raw?.id || validIds.has(String(raw.id))) continue;
    const isLocked = raw?.type === 'locked';
    const entityId = String(raw?.entityId || '');
    const isCourse = raw?.type === 'course';
    const entity = isLocked ? null : (isCourse ? courseById.get(entityId) : assignmentById.get(entityId));
    if (!isLocked && !entity) continue;
    const type = isLocked ? 'locked' : (isCourse ? 'course' : assignmentNodeType(entity.type));
    const x = Number(raw?.position?.x);
    const y = Number(raw?.position?.y);
    nodes.push({
      id: String(raw.id),
      type,
      ...(isLocked ? {} : { entityId }),
      position: {
        x: Number.isFinite(x) ? x : 0,
        y: Number.isFinite(y) ? y : 0,
      },
      settings: raw?.settings && typeof raw.settings === 'object' ? raw.settings : undefined,
    });
    validIds.add(String(raw.id));
  }
  const edges = (Array.isArray(document.edges) ? document.edges : [])
    .filter((edge) => edge?.id && validIds.has(String(edge.source)) && validIds.has(String(edge.target)) && String(edge.source) !== String(edge.target))
    .map((edge) => ({
      id: String(edge.id),
      source: String(edge.source),
      target: String(edge.target),
      sourceHandle: edge.sourceHandle || 'out',
      targetHandle: edge.targetHandle || 'in',
      ...(edge?.settings && typeof edge.settings === 'object' ? { settings: edge.settings } : {}),
    }));
  return {
    schemaVersion: COURSE_MAP_SCHEMA_VERSION,
    viewport: {
      x: Number(document?.viewport?.x) || 0,
      y: Number(document?.viewport?.y) || 0,
      zoom: Number(document?.viewport?.zoom) || 1,
    },
    nodes,
    edges,
  };
}

export function findUnplacedEntities(document, courses, assignments) {
  const placed = new Set((document?.nodes || []).map((node) => String(node.entityId || '')));
  const result = [];
  for (const course of Array.isArray(courses) ? courses : []) {
    if (!placed.has(String(course.id))) result.push({ kind: 'course', entity: course, type: 'course' });
  }
  for (const assignment of Array.isArray(assignments) ? assignments : []) {
    if (!placed.has(String(assignment.id))) result.push({ kind: 'assignment', entity: assignment, type: assignmentNodeType(assignment.type) });
  }
  return result;
}

export function serializeCourseMap(nodes, edges, viewport) {
  return {
    schemaVersion: COURSE_MAP_SCHEMA_VERSION,
    viewport: {
      x: Number(viewport?.x) || 0,
      y: Number(viewport?.y) || 0,
      zoom: Number(viewport?.zoom) || 1,
    },
    nodes: (nodes || []).map((node) => ({
      id: String(node.id),
      type: String(node.type),
      ...(node.type === 'locked' ? {} : { entityId: String(node.entityId || node.data?.entityId || '') }),
      position: {
        x: Number(node.position?.x) || 0,
        y: Number(node.position?.y) || 0,
      },
      ...(node.settings || node.data?.settings ? { settings: node.settings || node.data?.settings } : {}),
    })),
    edges: (edges || []).map((edge) => ({
      id: String(edge.id),
      source: String(edge.source),
      sourceHandle: edge.sourceHandle || 'out',
      target: String(edge.target),
      targetHandle: edge.targetHandle || 'in',
      ...(edge?.settings && typeof edge.settings === 'object' ? { settings: edge.settings } : {}),
    })),
  };
}

export function wouldCreateCycle(nodes, edges, source, target) {
  if (!source || !target || source === target) return true;
  const adjacency = new Map((nodes || []).map((node) => [String(node.id), []]));
  for (const edge of edges || []) {
    const list = adjacency.get(String(edge.source));
    if (list) list.push(String(edge.target));
  }
  const pending = [String(target)];
  const seen = new Set();
  while (pending.length) {
    const current = pending.pop();
    if (current === String(source)) return true;
    if (seen.has(current)) continue;
    seen.add(current);
    for (const next of adjacency.get(current) || []) pending.push(next);
  }
  return false;
}
