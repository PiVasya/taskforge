const CACHE_SCHEMA = 5;
const CACHE_PREFIX = 'taskforge.course-map.cache.v5';
const CACHE_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;
const CACHE_MAX_LOCAL_CHARS = 650_000;
const IDB_NAME = 'taskforge-course-map-cache-v1';
const IDB_STORE = 'maps';
const memory = new Map();
let dbPromise = null;

function openIndexedDb() {
  if (typeof indexedDB === 'undefined') return Promise.resolve(null);
  if (dbPromise) return dbPromise;
  dbPromise = new Promise((resolve) => {
    try {
      const request = indexedDB.open(IDB_NAME, 1);
      request.onupgradeneeded = () => {
        const db = request.result;
        if (!db.objectStoreNames.contains(IDB_STORE)) db.createObjectStore(IDB_STORE);
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => resolve(null);
      request.onblocked = () => resolve(null);
    } catch {
      resolve(null);
    }
  });
  return dbPromise;
}

async function idbGet(cacheKey) {
  const db = await openIndexedDb();
  if (!db) return null;
  return new Promise((resolve) => {
    try {
      const tx = db.transaction(IDB_STORE, 'readonly');
      const request = tx.objectStore(IDB_STORE).get(cacheKey);
      request.onsuccess = () => resolve(request.result || null);
      request.onerror = () => resolve(null);
    } catch {
      resolve(null);
    }
  });
}

async function idbSet(cacheKey, value) {
  const db = await openIndexedDb();
  if (!db) return;
  await new Promise((resolve) => {
    try {
      const tx = db.transaction(IDB_STORE, 'readwrite');
      tx.objectStore(IDB_STORE).put(value, cacheKey);
      tx.oncomplete = () => resolve();
      tx.onerror = () => resolve();
      tx.onabort = () => resolve();
    } catch {
      resolve();
    }
  });
}

async function idbDelete(cacheKey) {
  const db = await openIndexedDb();
  if (!db) return;
  await new Promise((resolve) => {
    try {
      const tx = db.transaction(IDB_STORE, 'readwrite');
      tx.objectStore(IDB_STORE).delete(cacheKey);
      tx.oncomplete = () => resolve();
      tx.onerror = () => resolve();
      tx.onabort = () => resolve();
    } catch {
      resolve();
    }
  });
}

async function idbClear() {
  const db = await openIndexedDb();
  if (!db) return;
  await new Promise((resolve) => {
    try {
      const tx = db.transaction(IDB_STORE, 'readwrite');
      tx.objectStore(IDB_STORE).clear();
      tx.oncomplete = () => resolve();
      tx.onerror = () => resolve();
      tx.onabort = () => resolve();
    } catch {
      resolve();
    }
  });
}

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


function courseSummary(item) {
  if (!item?.id) return null;
  return {
    id: item.id,
    parentCourseId: item.parentCourseId || null,
    title: item.title || '',
    description: item.description || '',
    isPublic: item.isPublic !== false,
    isHiddenFromStudents: item.isHiddenFromStudents === true,
    sort: Number(item.sort) || 0,
  };
}

function normalize(payload, expectedMode = '') {
  if (!payload || payload.schema !== CACHE_SCHEMA || !payload.mapRecord) return null;
  const sourceMode = clean(payload.sourceMode || (payload.editorMode ? 'editor' : 'learner'));
  if (expectedMode && sourceMode !== expectedMode) return null;
  const savedAt = Number(payload.savedAt || 0);
  if (!savedAt || Date.now() - savedAt > CACHE_MAX_AGE_MS) return null;
  return {
    ...payload,
    dirty: payload.dirty === true,
    assignments: Array.isArray(payload.assignments) ? payload.assignments.filter(Boolean) : [],
    courses: Array.isArray(payload.courses) ? payload.courses.filter(Boolean) : [],
    aliases: Array.isArray(payload.aliases) ? payload.aliases.map(clean).filter(Boolean) : [],
    pendingRevealNodeIds: Array.isArray(payload.pendingRevealNodeIds) ? payload.pendingRevealNodeIds.map(clean).filter(Boolean) : [],
  };
}

function readStoredPayload(target, cacheKey, editorMode, userId) {
  const expectedMode = modeKey(editorMode);
  const parsed = JSON.parse(target.getItem(cacheKey) || 'null');
  if (parsed?.schema === CACHE_SCHEMA && parsed?.aliasTo) {
    return normalize(JSON.parse(target.getItem(key(parsed.aliasTo, editorMode, userId)) || 'null'), expectedMode);
  }
  return normalize(parsed, expectedMode);
}


export function readCourseMapLocalCache({ courseId, editorMode = false, userId = '' } = {}) {
  const cacheKey = key(courseId, editorMode, userId);
  const inMemory = normalize(memory.get(cacheKey), modeKey(editorMode));
  if (inMemory) return { ...inMemory, cacheLayer: 'memory' };
  const target = storage();
  if (!target) return null;
  try {
    const parsed = readStoredPayload(target, cacheKey, editorMode, userId);
    if (!parsed) {
      target.removeItem(cacheKey);
      return null;
    }
    memory.set(cacheKey, parsed);
    return { ...parsed, cacheLayer: 'localStorage' };
  } catch {
    try { target.removeItem(cacheKey); } catch {}
    return null;
  }
}

export async function readCourseMapLocalCacheAsync({ courseId, editorMode = false, userId = '' } = {}) {
  const immediate = readCourseMapLocalCache({ courseId, editorMode, userId });
  if (immediate) return immediate;
  const cacheKey = key(courseId, editorMode, userId);
  try {
    let stored = await idbGet(cacheKey);
    if (stored?.schema === CACHE_SCHEMA && stored?.aliasTo) stored = await idbGet(key(stored.aliasTo, editorMode, userId));
    const parsed = normalize(stored, modeKey(editorMode));
    if (!parsed) {
      await idbDelete(cacheKey);
      return null;
    }
    for (const id of new Set([courseId, parsed.rootCourseId, ...(parsed.aliases || [])].map(clean).filter(Boolean))) {
      memory.set(key(id, editorMode, userId), parsed);
    }
    return { ...parsed, cacheLayer: 'indexedDB' };
  } catch {
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
  courses = [],
  aliases = [],
  dirty = false,
  pendingRevealNodeIds = [],
} = {}) {
  const requestedId = clean(courseId);
  const rootId = clean(rootCourseId || mapRecord?.rootCourseId || requestedId);
  if (!requestedId || !mapRecord) return;

  const aliasIds = new Set([
    requestedId,
    ...(rootId === requestedId ? [rootId] : []),
    clean(mapRecord?.requestedCourseId),
    ...aliases.map(clean),
    ...assignments.map((item) => clean(item?.courseId)),
    ...courses.map((item) => clean(item?.id)),
  ].filter(Boolean));

  const payload = {
    schema: CACHE_SCHEMA,
    savedAt: Date.now(),
    requestedCourseId: requestedId,
    rootCourseId: rootId,
    editorMode: Boolean(editorMode),
    sourceMode: modeKey(editorMode),
    dirty: dirty === true,
    aliases: Array.from(aliasIds),
    pendingRevealNodeIds: Array.from(new Set((pendingRevealNodeIds || []).map(clean).filter(Boolean))),
    mapRecord,
    assignments: assignments.map(assignmentSummary).filter(Boolean),
    courses: courses.map(courseSummary).filter(Boolean),
  };

  const target = storage();
  const localNodeCount = Array.isArray(mapRecord?.document?.nodes) ? mapRecord.document.nodes.length : 0;
  const localEdgeCount = Array.isArray(mapRecord?.document?.edges) ? mapRecord.document.edges.length : 0;
  const canTryLocalStorage = localNodeCount <= 500 && localEdgeCount <= 1000;
  let encoded = null;
  if (canTryLocalStorage) {
    try { encoded = JSON.stringify(payload); } catch {}
  }
  const primaryId = requestedId;
  const primaryCacheKey = key(primaryId, editorMode, userId);
  const canPersist = Boolean(target && encoded && encoded.length <= CACHE_MAX_LOCAL_CHARS);
  let persistedPrimary = false;
  if (canPersist) {
    try {
      target.setItem(primaryCacheKey, encoded);
      persistedPrimary = true;
    } catch {}
  }
  for (const id of aliasIds) {
    const cacheKey = key(id, editorMode, userId);
    memory.set(cacheKey, payload);
    if (!persistedPrimary) {
      try { target?.removeItem(cacheKey); } catch {}
    } else if (cacheKey !== primaryCacheKey) {
      try {
        target.setItem(cacheKey, JSON.stringify({ schema: CACHE_SCHEMA, savedAt: payload.savedAt, aliasTo: primaryId }));
      } catch {}
    }
  }

  void idbSet(primaryCacheKey, payload);
  for (const id of aliasIds) {
    const aliasKey = key(id, editorMode, userId);
    if (aliasKey === primaryCacheKey) continue;
    void idbSet(aliasKey, { schema: CACHE_SCHEMA, savedAt: payload.savedAt, aliasTo: primaryId });
  }
}

export function clearCourseMapLocalCache({ courseId, editorMode, userId = '' } = {}) {
  const targetId = clean(courseId);
  if (!targetId) return;
  const modes = typeof editorMode === 'boolean' ? [editorMode] : [false, true];
  const target = storage();
  for (const mode of modes) {
    const directKey = key(targetId, mode, userId);
    const cached = normalize(memory.get(directKey), modeKey(mode)) || readCourseMapLocalCache({ courseId: targetId, editorMode: mode, userId });
    const ids = new Set([targetId, ...(cached?.aliases || [])]);
    for (const id of ids) {
      const cacheKey = key(id, mode, userId);
      memory.delete(cacheKey);
      try { target?.removeItem(cacheKey); } catch {}
      void idbDelete(cacheKey);
    }
  }
}

export function clearAllCourseMapLocalCaches() {
  memory.clear();
  void idbClear();
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
