const CACHE_SCHEMA = 2;
const CACHE_PREFIX = 'taskforge.course-map.cache.v2';
const CACHE_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;
const CACHE_MAX_LOCAL_CHARS = 3_800_000;
const memory = new Map();

function clean(value) {
  return String(value || '').trim();
}

function userKey(userId) {
  return clean(userId) || 'anonymous';
}

function modeKey(editorMode) {
  return editorMode ? 'editor' : 'learner';
}

function key(courseId, editorMode, userId) {
  return `${CACHE_PREFIX}:${modeKey(editorMode)}:${userKey(userId)}:${clean(courseId)}`;
}

function storage() {
  if (typeof window === 'undefined') return null;
  try { return window.localStorage; } catch { return null; }
}

function assignmentSummary(item) {
  if (!item?.id) return null;
  return {
    id: item.id,
    courseId: item.courseId,
    title: item.title || '',
    description: item.description || '',
    type: item.type || 'code-test',
    tags: item.tags || '',
    sort: Number(item.sort) || 0,
    isVisible: item.isVisible !== false,
    canEdit: item.canEdit === true,
    solvedByCurrentUser: item.solvedByCurrentUser === true,
    isSolved: item.isSolved === true,
    progressStatus: item.progressStatus || null,
  };
}

function normalize(payload) {
  if (!payload || payload.schema !== CACHE_SCHEMA || !payload.mapRecord) return null;
  const savedAt = Number(payload.savedAt || 0);
  if (!savedAt || Date.now() - savedAt > CACHE_MAX_AGE_MS) return null;
  return {
    ...payload,
    dirty: payload.dirty === true,
    assignments: Array.isArray(payload.assignments) ? payload.assignments.filter(Boolean) : [],
    aliases: Array.isArray(payload.aliases) ? payload.aliases.map(clean).filter(Boolean) : [],
  };
}

function readStoredPayload(target, cacheKey, editorMode, userId) {
  const parsed = JSON.parse(target.getItem(cacheKey) || 'null');
  if (parsed?.schema === CACHE_SCHEMA && parsed?.aliasTo) {
    return normalize(JSON.parse(target.getItem(key(parsed.aliasTo, editorMode, userId)) || 'null'));
  }
  return normalize(parsed);
}

export function readCourseMapLocalCache({ courseId, editorMode = false, userId = '' } = {}) {
  const cacheKey = key(courseId, editorMode, userId);
  const inMemory = normalize(memory.get(cacheKey));
  if (inMemory) return inMemory;
  const target = storage();
  if (!target) return null;
  try {
    const parsed = readStoredPayload(target, cacheKey, editorMode, userId);
    if (!parsed) {
      target.removeItem(cacheKey);
      return null;
    }
    memory.set(cacheKey, parsed);
    return parsed;
  } catch {
    try { target.removeItem(cacheKey); } catch {}
    return null;
  }
}

export function writeCourseMapLocalCache({
  courseId,
  rootCourseId,
  editorMode = false,
  userId = '',
  mapRecord,
  assignments = [],
  aliases = [],
  dirty = false,
} = {}) {
  const requestedId = clean(courseId);
  const rootId = clean(rootCourseId || mapRecord?.rootCourseId || requestedId);
  if (!requestedId || !mapRecord) return;

  const aliasIds = new Set([
    requestedId,
    rootId,
    clean(mapRecord?.requestedCourseId),
    ...aliases.map(clean),
    ...assignments.map((item) => clean(item?.courseId)),
  ].filter(Boolean));

  const payload = {
    schema: CACHE_SCHEMA,
    savedAt: Date.now(),
    requestedCourseId: requestedId,
    rootCourseId: rootId,
    editorMode: Boolean(editorMode),
    dirty: dirty === true,
    aliases: Array.from(aliasIds),
    mapRecord,
    assignments: assignments.map(assignmentSummary).filter(Boolean),
  };

  const target = storage();
  let encoded = null;
  try { encoded = JSON.stringify(payload); } catch {}
  const rootCacheKey = key(rootId, editorMode, userId);
  const canPersist = Boolean(target && encoded && encoded.length <= CACHE_MAX_LOCAL_CHARS);
  let persistedRoot = false;
  if (canPersist) {
    try {
      target.setItem(rootCacheKey, encoded);
      persistedRoot = true;
    } catch {}
  }
  for (const id of aliasIds) {
    const cacheKey = key(id, editorMode, userId);
    memory.set(cacheKey, payload);
    if (!persistedRoot) {
      try { target?.removeItem(cacheKey); } catch {}
      continue;
    }
    if (cacheKey === rootCacheKey) continue;
    try {
      target.setItem(cacheKey, JSON.stringify({ schema: CACHE_SCHEMA, savedAt: payload.savedAt, aliasTo: rootId }));
    } catch {}
  }
}

export function clearCourseMapLocalCache({ courseId, editorMode, userId = '' } = {}) {
  const targetId = clean(courseId);
  if (!targetId) return;
  const modes = typeof editorMode === 'boolean' ? [editorMode] : [false, true];
  const target = storage();
  for (const mode of modes) {
    const directKey = key(targetId, mode, userId);
    const cached = normalize(memory.get(directKey)) || readCourseMapLocalCache({ courseId: targetId, editorMode: mode, userId });
    const ids = new Set([targetId, ...(cached?.aliases || [])]);
    for (const id of ids) {
      const cacheKey = key(id, mode, userId);
      memory.delete(cacheKey);
      try { target?.removeItem(cacheKey); } catch {}
    }
  }
}

export function clearAllCourseMapLocalCaches() {
  memory.clear();
  const target = storage();
  if (!target) return;
  try {
    const remove = [];
    for (let index = 0; index < target.length; index += 1) {
      const itemKey = target.key(index);
      if (itemKey?.startsWith('taskforge.course-map.cache.')) remove.push(itemKey);
    }
    remove.forEach((itemKey) => target.removeItem(itemKey));
  } catch {}
}
