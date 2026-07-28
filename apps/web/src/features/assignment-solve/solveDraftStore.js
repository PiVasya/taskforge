import { useCallback, useSyncExternalStore } from 'react';

const stores = new Map();
const EMPTY_DRAFT = Object.freeze({ assignmentId: '', code: '', language: 'cpp', hydrated: false });

function sameId(left, right) {
  return String(left || '').trim().toLowerCase() === String(right || '').trim().toLowerCase();
}

function storageKey(assignmentId, version = 3) {
  return `solve-draft:v${version}:${assignmentId}`;
}

function readPersisted(assignmentId) {
  if (!assignmentId) return null;
  try {
    for (const version of [3, 2]) {
      const raw = localStorage.getItem(storageKey(assignmentId, version));
      if (!raw) continue;
      const parsed = JSON.parse(raw);
      if (parsed && sameId(parsed.assignmentId, assignmentId)) return parsed;
    }
    return null;
  } catch {
    return null;
  }
}

function persist(snapshot) {
  if (!snapshot.assignmentId || !snapshot.hydrated) return;
  try {
    localStorage.setItem(storageKey(snapshot.assignmentId), JSON.stringify({
      assignmentId: snapshot.assignmentId,
      code: snapshot.code,
      language: snapshot.language,
      updatedAt: new Date().toISOString(),
      version: 3,
    }));
  } catch {}
}

function createStore(assignmentId) {
  let snapshot = Object.freeze({ ...EMPTY_DRAFT, assignmentId: String(assignmentId || '') });
  const listeners = new Set();
  let persistTimer = null;

  const emit = () => listeners.forEach((listener) => listener());
  const update = (patch, { persistNow = false } = {}) => {
    const next = typeof patch === 'function' ? patch(snapshot) : { ...snapshot, ...patch };
    const normalized = Object.freeze({
      assignmentId: String(next.assignmentId || assignmentId || ''),
      code: typeof next.code === 'string' ? next.code : '',
      language: String(next.language || 'cpp'),
      hydrated: Boolean(next.hydrated),
    });
    if (
      normalized.assignmentId === snapshot.assignmentId
      && normalized.code === snapshot.code
      && normalized.language === snapshot.language
      && normalized.hydrated === snapshot.hydrated
    ) return snapshot;
    snapshot = normalized;
    emit();
    if (persistTimer) clearTimeout(persistTimer);
    if (persistNow) persist(snapshot);
    else persistTimer = setTimeout(() => persist(snapshot), 250);
    return snapshot;
  };

  return {
    getSnapshot: () => snapshot,
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    initialize({ code = '', language = 'cpp', force = false } = {}) {
      if (snapshot.hydrated && !force) return snapshot;
      const persisted = readPersisted(assignmentId);
      return update({
        assignmentId,
        code: typeof persisted?.code === 'string' ? persisted.code : code,
        language: String(persisted?.language || language || 'cpp'),
        hydrated: true,
      }, { persistNow: true });
    },
    setCode(code) { return update({ code: typeof code === 'string' ? code : '' }); },
    setLanguage(language) { return update({ language: String(language || 'cpp') }); },
    replace(draft) { return update({ ...draft, assignmentId, hydrated: true }, { persistNow: true }); },
    flush() {
      if (persistTimer) clearTimeout(persistTimer);
      persistTimer = null;
      persist(snapshot);
    },
    destroy() {
      if (persistTimer) clearTimeout(persistTimer);
      listeners.clear();
    },
  };
}

export function getSolveDraftStore(assignmentId) {
  const key = String(assignmentId || '');
  if (!key) return null;
  let store = stores.get(key);
  if (!store) {
    store = createStore(key);
    stores.set(key, store);
  }
  return store;
}

export function getSolveDraftSnapshot(assignmentId) {
  return getSolveDraftStore(assignmentId)?.getSnapshot() || EMPTY_DRAFT;
}

export function initializeSolveDraft(assignmentId, draft) {
  return getSolveDraftStore(assignmentId)?.initialize(draft) || EMPTY_DRAFT;
}

export function useSolveDraft(assignmentId) {
  const store = getSolveDraftStore(assignmentId);
  const subscribe = useCallback((listener) => (store ? store.subscribe(listener) : () => {}), [store]);
  const getSnapshot = useCallback(() => store?.getSnapshot() || EMPTY_DRAFT, [store]);
  return useSyncExternalStore(subscribe, getSnapshot, () => EMPTY_DRAFT);
}

export function flushSolveDraft(assignmentId) {
  getSolveDraftStore(assignmentId)?.flush();
}

export function releaseSolveDraftStore(assignmentId) {
  const key = String(assignmentId || '');
  if (!key) return;
  const store = stores.get(key);
  if (!store) return;
  store.flush();
  store.destroy();
  stores.delete(key);
}
