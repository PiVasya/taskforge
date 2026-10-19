import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const srcRoot = new URL('../../src/features/course-assignments/', import.meta.url);
const changesSource = await fs.readFile(new URL('courseMapEditorChanges.js', srcRoot), 'utf8');
const changes = await import(`data:text/javascript;base64,${Buffer.from(changesSource).toString('base64')}`);

const nodes = [{ id: 'a', position: { x: 100, y: 200 } }, { id: 'b', position: { x: 0, y: 0 } }];

test('React Flow mount, position reconciliation and dimension changes do not dirty editor', () => {
  const events = [
    { id: 'a', type: 'dimensions', dimensions: { width: 200, height: 100 } },
    { id: 'a', type: 'position', position: { x: 120, y: 240 }, dragging: false },
    { id: 'a', type: 'select', selected: true },
  ];
  assert.equal(changes.hasIntentionalNodeChanges(events, nodes, { ready: false }), false);
  assert.equal(changes.hasIntentionalNodeChanges(events, nodes, { ready: true }), false);
  assert.equal(changes.hasIntentionalNodeChanges([{ id: 'a', type: 'position', position: { x: 100, y: 200 }, dragging: true }], nodes, { ready: true }), false);
  assert.equal(changes.hasIntentionalNodeChanges([{ id: 'not-on-map', type: 'remove' }], nodes, { ready: true }), false);
});

test('real drag, keyboard nudge and deletion still dirty editor', () => {
  const move = [{ id: 'a', type: 'position', position: { x: 120, y: 200 }, dragging: false }];
  assert.equal(changes.hasIntentionalNodeChanges(move, nodes, { ready: true, dragging: true }), true);
  assert.equal(changes.hasIntentionalNodeChanges(move, nodes, { ready: true, keyboardMove: true }), true);
  assert.equal(changes.hasIntentionalNodeChanges([{ ...move[0], dragging: true }], nodes, { ready: true }), true);
  assert.equal(changes.hasIntentionalNodeChanges([{ id: 'b', type: 'remove' }], nodes, { ready: true }), true);
  assert.equal(changes.hasIntentionalNodeChanges([{ id: 'b', type: 'remove' }], nodes, { ready: false }), false);
});

// IndexedDB mock keeps writes pending briefly, so these tests exercise a real
// read/write/delete ordering problem rather than only the in-memory cache.
function indexedDbMock() {
  const stored = new Map();
  let writes = 0;
  let transactions = 0;
  const db = {
    objectStoreNames: { contains: () => true },
    transaction(_storeName, mode) {
      transactions += 1;
      const operations = [];
      const tx = {
        objectStore() {
          return {
            put(value, key) { operations.push(() => stored.set(key, structuredClone(value))); writes += 1; },
            delete(key) { operations.push(() => stored.delete(key)); writes += 1; },
            clear() { operations.push(() => stored.clear()); writes += 1; },
            get(key) {
              const req = {};
              setTimeout(() => { req.result = stored.get(key); req.onsuccess?.(); }, 2);
              return req;
            },
          };
        },
      };
      if (mode === 'readwrite') setTimeout(() => {
        for (const operation of operations) operation();
        tx.oncomplete?.();
      }, 4);
      return tx;
    },
  };
  return {
    stored,
    get transactions() { return transactions; },
    get writes() { return writes; },
    open() {
      const request = { result: db };
      queueMicrotask(() => request.onsuccess?.());
      return request;
    },
  };
}

const localStorage = new Map();
globalThis.window = { localStorage: {
  getItem: (key) => localStorage.get(key) ?? null,
  setItem: (key, value) => { localStorage.set(key, String(value)); },
  removeItem: (key) => { localStorage.delete(key); },
  key: (index) => [...localStorage.keys()][index] || null,
  get length() { return localStorage.size; },
} };
globalThis.indexedDB = indexedDbMock();

const cacheSource = (await fs.readFile(new URL('courseMapLocalCache.js', srcRoot), 'utf8'))
  .replace("import { courseMapConsole } from './courseMapDebug';",     'const courseMapConsole = (...args) => { (globalThis.__cacheDiagnostics ||= []).push(args); };');
let importCounter = 0;
async function importCache() {
  // Independent module instance simulates a tab reopened with an empty memory cache.
  return import(`data:text/javascript;base64,${Buffer.from(`${cacheSource}\n// ${++importCounter}`).toString('base64')}`);
}

function map(version, size) {
  return { version, document: {
    nodes: Array.from({ length: size }, (_, index) => ({ id: `node-${index}`, position: { x: index * 2, y: 0 } })),
    edges: [],
    viewport: { x: 0, y: 0, zoom: 1 },
  } };
}

const args = { courseId: 'olympiad', rootCourseId: 'olympiad', editorMode: true, userId: 'tester', aliases: ['olympiad-year'] };
const cacheKey = 'taskforge.course-map.cache.v6:editor:tester:olympiad';

test('clear waits for pending oversized draft writes before applying new server map', async () => {
  const cache = await importCache();
  cache.writeCourseMapLocalCache({ ...args, mapRecord: map(73, 198), dirty: true });
  cache.writeCourseMapLocalCache({ ...args, mapRecord: map(73, 198), dirty: true });
  await cache.clearCourseMapLocalCache(args);
  assert.equal(indexedDB.stored.has(cacheKey), false);
  await cache.writeCourseMapLocalCache({ ...args, mapRecord: map(78, 709), dirty: false });
  // Re-open with an empty memory cache.

  const cold = await importCache();
  const restored = await cold.readCourseMapLocalCacheAsync(args);
  assert.equal(restored.mapRecord.version, 78);
  assert.equal(restored.dirty, false);
  assert.equal(restored.mapRecord.document.nodes.length, 709);
  assert.ok(indexedDB.transactions < 15, 'aliases must be stored within one write transaction');
});

test('an already pending read cannot resurrect a draft after clearing it', async () => {
  const cold = await importCache();
  const pending = cold.readCourseMapLocalCacheAsync(args);
  await cold.clearCourseMapLocalCache(args);
  const restored = await pending;
  assert.equal(restored, null);
  assert.equal(indexedDB.stored.has(cacheKey), false);
});


test('server fetch must finish before any old draft is removed', async () => {
  let rejectFetch;
  const trace = [];
  const request = new Promise((_resolve, reject) => { rejectFetch = reject; });
  const result = changes.loadFreshEditorMap({
    fetchSnapshot: () => request,
    isCurrent: () => true,
    discardDraft: async () => { trace.push('discard'); },
    applySnapshot: () => { trace.push('apply'); return true; },
  });
  rejectFetch(new Error('network offline'));
  await assert.rejects(result, /network offline/);
  assert.deepEqual(trace, []);
});

test('latest map appears only after the stale draft is fully cleared', async () => {
  const trace = [];
  const result = await changes.loadFreshEditorMap({
    fetchSnapshot: async () => { trace.push('fetch'); return { version: 78 }; },
    isCurrent: () => true,
    discardDraft: async () => { trace.push('discard-start'); await new Promise((r) => setTimeout(r, 5)); trace.push('discard-end'); },
    applySnapshot: (snapshot) => { trace.push(`apply-v${snapshot.version}`); return true; },
  });
  assert.equal(result, true);
  assert.deepEqual(trace, ['fetch', 'discard-start', 'discard-end', 'apply-v78']);
});

test('stale server response does not discard a newer editor state', async () => {
  let current = true;
  const trace = [];
  const result = await changes.loadFreshEditorMap({
    fetchSnapshot: async () => { current = false; return { version: 78 }; },
    isCurrent: () => current,
    discardDraft: async () => { trace.push('discard'); },
    applySnapshot: () => { trace.push('apply'); },
  });
  assert.equal(result, false);
  assert.deepEqual(trace, []);
});


test('draft cache diagnostics record write, ordered clear and successful IndexedDB commit', async () => {
  globalThis.__cacheDiagnostics = [];
  const cache = await importCache();
  await cache.writeCourseMapLocalCache({ ...args, mapRecord: map(80, 709), dirty: true });
  await cache.clearCourseMapLocalCache(args);
  const events = globalThis.__cacheDiagnostics;
  const write = events.find(([event]) => event === 'CACHE_WRITE');
  const clear = events.find(([event]) => event === 'CACHE_CLEAR_BEGIN');
  const completed = events.filter(([event]) => event === 'CACHE_IDB_COMPLETE');
  assert.equal(write[1].version, 80);
  assert.equal(write[1].dirty, true);
  assert.equal(write[1].nodes, 709);
  assert.ok(clear);
  assert.equal(completed.at(-1)[1].committed, true);
  assert.ok(events.findIndex(([event]) => event === 'CACHE_WRITE') < events.findIndex(([event]) => event === 'CACHE_CLEAR_BEGIN'));
  assert.equal(JSON.stringify(events).includes('description'), false);
});
