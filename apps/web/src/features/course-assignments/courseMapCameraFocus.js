// Learner camera navigation is intentionally separate from the authored map viewport.
// The latter belongs to editors; the former is private to the current student.
const SOLVED_KEY = 'taskforge.course-map.recently-solved.v1';
const RETURN_PREFIX = 'taskforge.course-map.return.v1';
const HISTORY_MAX = 120;
const RETURN_MAX_AGE_MS = 2 * 60 * 60 * 1000;
const SOLVED_MAX_AGE_MS = 365 * 24 * 60 * 60 * 1000;

const clean = (value) => String(value || '').trim().toLowerCase();
const scopedUser = (value) => clean(value) || 'anonymous';

function getStorage(kind) {
  if (typeof window === 'undefined') return null;
  try { return kind === 'session' ? window.sessionStorage : window.localStorage; }
  catch { return null; }
}

function readJson(target, key, fallback) {
  if (!target) return fallback;
  try { return JSON.parse(target.getItem(key) || 'null') || fallback; }
  catch { return fallback; }
}

export function rememberSolvedForCourseMap({ userId, courseId, assignmentId, at = Date.now() } = {}) {
  const id = clean(assignmentId);
  if (!id) return;
  const target = getStorage('local');
  if (!target) return;
  const key = scopedUser(userId);
  const source = readJson(target, SOLVED_KEY, { schema: 1, users: {} });
  const users = source?.schema === 1 && source.users && typeof source.users === 'object'
    ? source.users : {};
  const previous = Array.isArray(users[key]) ? users[key] : [];
  const time = Number.isFinite(Number(at)) ? Number(at) : Date.now();
  const next = previous.filter((item) => clean(item?.assignmentId) !== id && time - Number(item?.at || 0) <= SOLVED_MAX_AGE_MS);
  next.push({ assignmentId: id, courseId: clean(courseId), at: time });
  users[key] = next.slice(-HISTORY_MAX);
  try { target.setItem(SOLVED_KEY, JSON.stringify({ schema: 1, users })); } catch {}
}

export function readRecentlySolvedForCourseMap(userId) {
  const source = readJson(getStorage('local'), SOLVED_KEY, null);
  const rows = source?.schema === 1 && Array.isArray(source.users?.[scopedUser(userId)])
    ? source.users[scopedUser(userId)] : [];
  const now = Date.now();
  return rows.filter((item) => clean(item?.assignmentId)
    && Number(item?.at) > 0 && Number(item.at) <= now + 60_000
    && now - Number(item.at) <= SOLVED_MAX_AGE_MS)
    .map((item) => ({ assignmentId: clean(item.assignmentId), courseId: clean(item.courseId), at: Number(item.at), localConfirmed: true }))
    .sort((a, b) => b.at - a.at);
}

function returnKey({ userId, rootCourseId } = {}) {
  return `${RETURN_PREFIX}:${scopedUser(userId)}:${clean(rootCourseId)}`;
}

export function rememberCourseMapReturn({ userId, rootCourseId, mapLocationKey, assignmentId } = {}) {
  const id = clean(assignmentId);
  const mapKey = String(mapLocationKey || '').trim();
  if (!id || !clean(rootCourseId) || !mapKey) return;
  try {
    getStorage('session')?.setItem(returnKey({ userId, rootCourseId }), JSON.stringify({ assignmentId: id, mapLocationKey: mapKey, at: Date.now() }));
  } catch {}
}

export function readCourseMapReturn({ userId, rootCourseId, mapLocationKey } = {}) {
  const target = getStorage('session');
  const record = readJson(target, returnKey({ userId, rootCourseId }), null);
  if (!record || String(record.mapLocationKey) !== String(mapLocationKey || '')) return '';
  if (!Number.isFinite(Number(record.at)) || Date.now() - Number(record.at) > RETURN_MAX_AGE_MS) return '';
  return clean(record.assignmentId);
}

export function clearCourseMapReturn({ userId, rootCourseId, mapLocationKey } = {}) {
  const target = getStorage('session');
  const key = returnKey({ userId, rootCourseId });
  const record = readJson(target, key, null);
  if (record && String(record.mapLocationKey) === String(mapLocationKey || '')) {
    try { target.removeItem(key); } catch {}
  }
}

function nodeAssignmentId(node) {
  return clean(node?.entityId || node?.data?.entityId || node?.data?.entity?.id);
}

function isSolved(node) {
  const entity = node?.data?.entity;
  return entity?.solvedByCurrentUser === true || entity?.isSolved === true
    || entity?.completedByCurrentUser === true || clean(entity?.progressStatus) === 'solved';
}

export function getAssignmentCameraNode(nodes, assignmentId) {
  const target = clean(assignmentId);
  if (!target) return null;
  return (Array.isArray(nodes) ? nodes : []).find((node) => node
    && node.type !== 'course' && node.type !== 'locked' && nodeAssignmentId(node) === target) || null;
}

export function learnerCameraRootNode(nodes, edges, rootCourseId) {
  const rows = (Array.isArray(nodes) ? nodes : []).filter(Boolean);
  if (!rows.length) return null;
  const wanted = clean(rootCourseId);
  const root = rows.find((node) => node.type === 'course' && nodeAssignmentId(node) === wanted);
  if (root) return root;
  const targets = new Set((Array.isArray(edges) ? edges : []).map((edge) => String(edge?.target || '')));
  return rows.find((node) => node.type === 'course' && !targets.has(String(node.id)))
    || rows.find((node) => !targets.has(String(node.id))) || rows[0];
}

// All inputs are user-specific and always intersected with nodes in this course.
// No remembered ID can center another student's map or an unrelated course.
export function chooseLearnerCameraTarget({ nodes, edges, rootCourseId, returnAssignmentId = '', localSolved = [], serverSolved = [], allowGraphFallback = true } = {}) {
  const rows = (Array.isArray(nodes) ? nodes : []).filter(Boolean);
  const byAssignment = new Map(rows.filter((node) => node.type !== 'course' && node.type !== 'locked')
    .map((node) => [nodeAssignmentId(node), node]).filter(([id]) => Boolean(id)));
  const direct = byAssignment.get(clean(returnAssignmentId));
  if (direct) return { nodeId: String(direct.id), reason: 'return-to-assignment' };

  const candidates = [
    ...(Array.isArray(localSolved) ? localSolved : []),
    ...(Array.isArray(serverSolved) ? serverSolved : []).map((item) => ({ ...item, serverConfirmed: true })),
  ].filter((item) => clean(item?.assignmentId) && Number.isFinite(Number(item?.at)))
    .sort((a, b) => Number(b.at) - Number(a.at));
  for (const item of candidates) {
    const node = byAssignment.get(clean(item.assignmentId));
    // The history endpoint is authoritative even if a cached projection still
    // says unsolved. A new local acceptance can also precede that projection.
    if (node && (isSolved(node) || item.serverConfirmed === true
      || (item.localConfirmed === true && Date.now() - Number(item.at) < 5 * 60 * 1000))) {
      return { nodeId: String(node.id), reason: 'last-accepted' };
    }
  }

  if (!allowGraphFallback) return null;
  const root = learnerCameraRootNode(rows, edges, rootCourseId);
  if (!root) return null;
  // Old accounts have solved flags but may have no dated local/history entry.
  // Use graph order as a deterministic fallback, never 'first unsolved'.
  const byId = new Map(rows.map((node) => [String(node.id), node]));
  const outgoing = new Map();
  for (const edge of Array.isArray(edges) ? edges : []) {
    const from = String(edge?.source || '');
    const to = String(edge?.target || '');
    if (!byId.has(from) || !byId.has(to)) continue;
    if (!outgoing.has(from)) outgoing.set(from, []);
    outgoing.get(from).push(to);
  }
  const queue = [String(root.id)];
  const seen = new Set();
  let latest = null;
  while (queue.length) {
    const id = String(queue.shift() || '');
    if (!id || seen.has(id)) continue;
    seen.add(id);
    const node = byId.get(id);
    if (!node || node.type === 'locked') continue;
    if (node.type !== 'course' && isSolved(node)) latest = node;
    for (const target of outgoing.get(id) || []) if (!seen.has(target)) queue.push(target);
  }
  return latest
    ? { nodeId: String(latest.id), reason: 'solved-graph-fallback' }
    : { nodeId: String(root.id), reason: 'course-start' };
}

export function collapsedCoursesHidingNode(nodeId, nodes, edges, collapsedIds) {
  const target = String(nodeId || '');
  const collapsed = new Set(Array.from(collapsedIds || []).map(String));
  if (!target || !collapsed.size) return [];
  const nodesById = new Map((nodes || []).map((node) => [String(node.id), node]));
  const outgoing = new Map();
  for (const edge of edges || []) {
    const from = String(edge?.source || '');
    const to = String(edge?.target || '');
    if (!nodesById.has(from) || !nodesById.has(to)) continue;
    if (!outgoing.has(from)) outgoing.set(from, []);
    outgoing.get(from).push(to);
  }
  const result = [];
  for (const courseId of collapsed) {
    const seen = new Set();
    const queue = [courseId];
    while (queue.length) {
      const current = String(queue.shift() || '');
      if (!current || seen.has(current)) continue;
      seen.add(current);
      if (current === target) { result.push(courseId); break; }
      if (current !== courseId && nodesById.get(current)?.type === 'course') continue;
      for (const next of outgoing.get(current) || []) queue.push(next);
    }
  }
  return result;
}
