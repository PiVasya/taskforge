import { courseMapConsole } from './courseMapDebug';

const CACHE_SCHEMA = 6;
const CACHE_PREFIX = 'taskforge.course-map.cache.v6';
const CACHE_FULL_REVALIDATE_MS = 60 * 1000;
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

// Serialize cache mutations: a delayed IndexedDB write must not restore a draft
// after the user has explicitly selected a fresh server version.
let idbMutationQueue = Promise.resolve();
let cacheRevision = 0;

function queueIdbMutation({ puts = [], deletes = [], clear = false, reason = 'unknown' } = {}) {
  const revision = cacheRevision;
  const startedAt = Date.now();
  courseMapConsole('CACHE_IDB_QUEUED', { reason, revision, puts: puts.length, deletes: deletes.length, clear });
  const mutate = async () => {
    const db = await openIndexedDb();
    if (!db) {
      courseMapConsole('CACHE_IDB_UNAVAILABLE', { reason, revision }, 'warn');
      return false;
    }
    const committed = await new Promise((resolve) => {
      try {
        const tx = db.transaction(IDB_STORE, 'readwrite');
        const store = tx.objectStore(IDB_STORE);
        if (clear) store.clear();
        for (const cacheKey of deletes) store.delete(cacheKey);
        for (const [cacheKey, value] of puts) store.put(value, cacheKey);
        tx.oncomplete = () => resolve(true);
        tx.onerror = () => resolve(false);
        tx.onabort = () => resolve(false);
      } catch {
        resolve(false);
      }
    });
    courseMapConsole('CACHE_IDB_COMPLETE', {
      reason, revision, committed, durationMs: Date.now() - startedAt,
      puts: puts.length, deletes: deletes.length, clear,
    }, committed ? 'info' : 'warn');
    return committed;
  };
  const pending = idbMutationQueue.then(mutate, mutate);
  idbMutationQueue = pending.catch(() => {});
  return pending;
}

async function idbGet(cacheKey) {
  await idbMutationQueue;
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
    fullSyncAt: Number(payload.fullSyncAt || payload.mapRecord?.fullSyncAt || 0),
    verifiedAt: Number(payload.verifiedAt || payload.mapRecord?.verifiedAt || payload.fullSyncAt || payload.mapRecord?.fullSyncAt || 0),
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


export function courseMapCacheNeedsFullRevalidation(payload, { force = false, now = Date.now() } = {}) {
  if (force) return true;
  const verifiedAt = Number(payload?.verifiedAt || payload?.mapRecord?.verifiedAt || payload?.fullSyncAt || payload?.mapRecord?.fullSyncAt || 0);
  if (!Number.isFinite(verifiedAt) || verifiedAt <= 0) return true;
  return Math.max(0, Number(now) || Date.now()) - verifiedAt >= CACHE_FULL_REVALIDATE_MS;
}

export function readCourseMapLocalCache({ courseId, editorMode = false, userId = '' } = {}) {
  const cacheKey = key(courseId, editorMode, userId);
  const inMemory = normalize(memory.get(cacheKey), modeKey(editorMode));
  if (inMemory) {
    courseMapConsole('CACHE_READ_MEMORY', { courseId: clean(courseId), mode: modeKey(editorMode), version: Number(inMemory.mapRecord?.version || 0), dirty: inMemory.dirty });
    return { ...inMemory, cacheLayer: 'memory' };
  }
  const target = storage();
  if (!target) return null;
  try {
    const parsed = readStoredPayload(target, cacheKey, editorMode, userId);
    if (!parsed) {
      target.removeItem(cacheKey);
      return null;
    }
    memory.set(cacheKey, parsed);
    courseMapConsole('CACHE_READ_LOCAL_STORAGE', { courseId: clean(courseId), mode: modeKey(editorMode), version: Number(parsed.mapRecord?.version || 0), dirty: parsed.dirty, nodes: parsed.mapRecord?.document?.nodes?.length || 0, edges: parsed.mapRecord?.document?.edges?.length || 0 });
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
  const startingRevision = cacheRevision;
  try {
    let stored = await idbGet(cacheKey);
    if (stored?.schema === CACHE_SCHEMA && stored?.aliasTo) stored = await idbGet(key(stored.aliasTo, editorMode, userId));
    // An async read begun before a clear/write cannot resurrect that old value.
    if (cacheRevision !== startingRevision) {
      courseMapConsole('CACHE_READ_SUPERSEDED', { courseId: clean(courseId), mode: modeKey(editorMode), startingRevision, currentRevision: cacheRevision });
      return readCourseMapLocalCache({ courseId, editorMode, userId });
    }
    const parsed = normalize(stored, modeKey(editorMode));
    if (!parsed) {
      await queueIdbMutation({ deletes: [cacheKey] });
      return null;
    }
    for (const id of new Set([courseId, parsed.rootCourseId, ...(parsed.aliases || [])].map(clean).filter(Boolean))) {
      memory.set(key(id, editorMode, userId), parsed);
    }
    courseMapConsole('CACHE_READ_IDB', { courseId: clean(courseId), mode: modeKey(editorMode), version: Number(parsed.mapRecord?.version || 0), dirty: parsed.dirty, nodes: parsed.mapRecord?.document?.nodes?.length || 0, edges: parsed.mapRecord?.document?.edges?.length || 0 });
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
    fullSyncAt: Number(mapRecord?.fullSyncAt || 0),
    verifiedAt: Number(mapRecord?.verifiedAt || mapRecord?.fullSyncAt || 0),
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

  cacheRevision += 1;
  courseMapConsole('CACHE_WRITE', {
    courseId: requestedId, mode: modeKey(editorMode), revision: cacheRevision,
    version: Number(mapRecord?.version || 0), dirty: dirty === true,
    nodes: localNodeCount, edges: localEdgeCount, aliases: aliasIds.size,
    localStorageSaved: persistedPrimary,
    localStorageEligible: canTryLocalStorage,
    payloadChars: encoded?.length || 0,
  });
  const puts = [[primaryCacheKey, payload]];
  for (const id of aliasIds) {
    const aliasKey = key(id, editorMode, userId);
    if (aliasKey === primaryCacheKey) continue;
    puts.push([aliasKey, { schema: CACHE_SCHEMA, savedAt: payload.savedAt, aliasTo: primaryId }]);
  }
  return queueIdbMutation({ puts, reason: 'map-write' });
}

export function clearCourseMapLocalCache({ courseId, editorMode, userId = '' } = {}) {
  const targetId = clean(courseId);
  if (!targetId) return Promise.resolve();
  const modes = typeof editorMode === 'boolean' ? [editorMode] : [false, true];
  const target = storage();
  const deletes = new Set();
  cacheRevision += 1;
  courseMapConsole('CACHE_CLEAR_BEGIN', { courseId: targetId, modes: modes.map(modeKey), revision: cacheRevision });
  for (const mode of modes) {
    const directKey = key(targetId, mode, userId);
    const cached = normalize(memory.get(directKey), modeKey(mode)) || readCourseMapLocalCache({ courseId: targetId, editorMode: mode, userId });
    const ids = new Set([targetId, ...(cached?.aliases || [])]);
    for (const id of ids) {
      const cacheKey = key(id, mode, userId);
      memory.delete(cacheKey);
      try { target?.removeItem(cacheKey); } catch {}
      deletes.add(cacheKey);
    }
  }
  const clearRevision = cacheRevision;
  const result = queueIdbMutation({ deletes: [...deletes], reason: 'map-clear' });
  void result.then((committed) => courseMapConsole('CACHE_CLEAR_COMPLETE', {
    courseId: targetId, revision: clearRevision, keys: deletes.size, committed,
  }, committed ? 'info' : 'warn'));
  return result;
}

export function clearAllCourseMapLocalCaches() {
  memory.clear();
  cacheRevision += 1;
  void queueIdbMutation({ clear: true, reason: 'all-maps-clear' });
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
