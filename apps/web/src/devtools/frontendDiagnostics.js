import {
  DEFAULT_FRONTEND_LOG_LIMIT_MB,
  approximateDiagnosticBytes,
  clampFrontendLogLimitMb,
  describeDiagnosticTarget,
  sanitizeDiagnosticUrl,
  sanitizeDiagnosticValue,
} from './frontendDiagnosticsModel';

const DB_NAME = 'taskforge-frontend-diagnostics';
const DB_VERSION = 1;
const EVENTS_STORE = 'events';
const META_STORE = 'meta';
const ENABLED_KEY = 'tfDeveloperMode';
const LIMIT_KEY = 'tfFrontendLogLimitMb';
const CONFIG_EVENT = 'tf-frontend-diagnostics-config';
const MAX_QUEUE_BYTES = 2 * 1024 * 1024;
const MAX_QUEUE_ITEMS = 1200;
const FLUSH_DELAY_MS = 750;
const MEMORY_SAMPLE_MS = 15_000;
const SESSION_ID = (() => {
  try { return crypto.randomUUID(); } catch { return `session-${Date.now()}-${Math.random().toString(36).slice(2)}`; }
})();

let dbPromise = null;
let queue = [];
let queueBytes = 0;
let flushTimer = null;
let flushPromise = null;
let droppedInMemory = 0;
let storageError = '';
let runtimeStop = null;
let configListenerInstalled = false;
let configLoaded = false;
let cachedConfig = { enabled: false, limitMb: DEFAULT_FRONTEND_LOG_LIMIT_MB };

function browserNow() {
  return typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : 0;
}

function isBrowser() {
  return typeof window !== 'undefined' && typeof indexedDB !== 'undefined';
}

function refreshCachedConfig() {
  if (typeof window === 'undefined') {
    cachedConfig = { enabled: false, limitMb: DEFAULT_FRONTEND_LOG_LIMIT_MB };
    configLoaded = true;
    return cachedConfig;
  }
  let enabled = false;
  let limitMb = DEFAULT_FRONTEND_LOG_LIMIT_MB;
  try { enabled = window.localStorage.getItem(ENABLED_KEY) === '1'; } catch {}
  try { limitMb = clampFrontendLogLimitMb(window.localStorage.getItem(LIMIT_KEY)); } catch {}
  cachedConfig = { enabled, limitMb };
  configLoaded = true;
  return cachedConfig;
}

export function getFrontendDiagnosticsConfig() {
  if (!configLoaded) refreshCachedConfig();
  return { ...cachedConfig };
}

function emitConfigChanged() {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new CustomEvent(CONFIG_EVENT, { detail: getFrontendDiagnosticsConfig() }));
}

export function setFrontendDiagnosticsEnabled(enabled) {
  if (typeof window === 'undefined') return;
  try { window.localStorage.setItem(ENABLED_KEY, enabled ? '1' : '0'); } catch {}
  cachedConfig = { ...getFrontendDiagnosticsConfig(), enabled: Boolean(enabled) };
  configLoaded = true;
  emitConfigChanged();
}

export function setFrontendDiagnosticsLimitMb(limitMb) {
  if (typeof window === 'undefined') return;
  const next = clampFrontendLogLimitMb(limitMb);
  try { window.localStorage.setItem(LIMIT_KEY, String(next)); } catch {}
  cachedConfig = { ...getFrontendDiagnosticsConfig(), limitMb: next };
  configLoaded = true;
  emitConfigChanged();
  trimToLimit(next * 1024 * 1024).catch(() => {});
}

function openDatabase() {
  if (!isBrowser()) return Promise.reject(new Error('IndexedDB unavailable'));
  if (dbPromise) return dbPromise;
  dbPromise = new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains(EVENTS_STORE)) {
        const store = db.createObjectStore(EVENTS_STORE, { keyPath: 'id', autoIncrement: true });
        store.createIndex('ts', 'ts', { unique: false });
      }
      if (!db.objectStoreNames.contains(META_STORE)) db.createObjectStore(META_STORE, { keyPath: 'key' });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error || new Error('IndexedDB open failed'));
    request.onblocked = () => reject(new Error('IndexedDB upgrade blocked'));
  }).catch((error) => {
    dbPromise = null;
    throw error;
  });
  return dbPromise;
}

function transactionDone(tx) {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error || new Error('IndexedDB transaction failed'));
    tx.onabort = () => reject(tx.error || new Error('IndexedDB transaction aborted'));
  });
}

function requestValue(request) {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error || new Error('IndexedDB request failed'));
  });
}

async function readTotalBytes(db) {
  const tx = db.transaction(META_STORE, 'readonly');
  const done = transactionDone(tx);
  const value = await requestValue(tx.objectStore(META_STORE).get('totalBytes')).catch(() => null);
  await done;
  return Number(value?.value || 0);
}

async function appendBatch(batch) {
  if (!batch.length) return;
  const db = await openDatabase();
  const previousTotal = await readTotalBytes(db);
  let total = previousTotal;
  const tx = db.transaction([EVENTS_STORE, META_STORE], 'readwrite');
  const done = transactionDone(tx);
  const events = tx.objectStore(EVENTS_STORE);
  const meta = tx.objectStore(META_STORE);
  for (const record of batch) {
    events.add(record);
    total += Number(record.sizeBytes || 0);
  }
  meta.put({ key: 'totalBytes', value: total, updatedAt: Date.now() });
  await done;
  const { limitMb } = getFrontendDiagnosticsConfig();
  if (total > limitMb * 1024 * 1024) await trimToLimit(limitMb * 1024 * 1024);
}

async function trimToLimit(maxBytes) {
  if (!isBrowser()) return;
  const db = await openDatabase();
  let total = await readTotalBytes(db);
  const maxDeletesPerPass = 2000;
  const maxBytesPerPass = 8 * 1024 * 1024;

  while (total > maxBytes) {
    const tx = db.transaction([EVENTS_STORE, META_STORE], 'readwrite');
    const done = transactionDone(tx);
    const events = tx.objectStore(EVENTS_STORE);
    const meta = tx.objectStore(META_STORE);
    let deleted = 0;
    let removedBytes = 0;

    await new Promise((resolve, reject) => {
      const cursorRequest = events.openCursor();
      cursorRequest.onerror = () => reject(cursorRequest.error || new Error('IndexedDB cursor failed'));
      cursorRequest.onsuccess = () => {
        const cursor = cursorRequest.result;
        const passLimitReached = deleted >= maxDeletesPerPass || removedBytes >= maxBytesPerPass;
        if (!cursor || total <= maxBytes || passLimitReached) {
          meta.put({ key: 'totalBytes', value: Math.max(0, total), updatedAt: Date.now() });
          resolve();
          return;
        }
        const size = Number(cursor.value?.sizeBytes || 0);
        total -= size;
        removedBytes += size;
        deleted += 1;
        cursor.delete();
        cursor.continue();
      };
    });
    await done;
    if (deleted === 0) break;
    await new Promise((resolve) => window.setTimeout(resolve, 0));
  }
}

function scheduleFlush() {
  if (flushTimer != null || typeof window === 'undefined') return;
  flushTimer = window.setTimeout(() => {
    flushTimer = null;
    flushFrontendDiagnostics().catch(() => {});
  }, FLUSH_DELAY_MS);
}

export async function flushFrontendDiagnostics() {
  if (!isBrowser()) return;
  if (flushPromise) return flushPromise;
  const batch = queue;
  if (!batch.length) return;
  queue = [];
  queueBytes = 0;
  flushPromise = appendBatch(batch)
    .catch(async (error) => {
      storageError = String(error?.message || error || 'IndexedDB write failed').slice(0, 500);
      try {
        const { limitMb } = getFrontendDiagnosticsConfig();
        await trimToLimit(Math.max(10, limitMb * 0.75) * 1024 * 1024);
        await appendBatch(batch.slice(-Math.min(batch.length, 200)));
      } catch {
        droppedInMemory += batch.length;
      }
    })
    .finally(() => { flushPromise = null; });
  return flushPromise;
}

function currentRoute() {
  if (typeof window === 'undefined') return '';
  return sanitizeDiagnosticUrl(`${window.location.pathname}${window.location.search || ''}${window.location.hash || ''}`);
}

export function logFrontendEvent(category, event, detail = null, level = 'info') {
  if (!getFrontendDiagnosticsConfig().enabled || !isBrowser()) return false;
  const base = {
    ts: Date.now(),
    monoMs: Math.round(browserNow()),
    sessionId: SESSION_ID,
    level: String(level || 'info').slice(0, 24),
    category: String(category || 'app').slice(0, 80),
    event: String(event || 'event').slice(0, 120),
    route: currentRoute(),
    detail: sanitizeDiagnosticValue(detail),
  };
  const sizeBytes = approximateDiagnosticBytes(base);
  const record = { ...base, sizeBytes };
  while (queue.length && (queue.length >= MAX_QUEUE_ITEMS || queueBytes + sizeBytes > MAX_QUEUE_BYTES)) {
    const removed = queue.shift();
    queueBytes -= Number(removed?.sizeBytes || 0);
    droppedInMemory += 1;
  }
  if (sizeBytes > MAX_QUEUE_BYTES) {
    droppedInMemory += 1;
    return false;
  }
  queue.push(record);
  queueBytes += sizeBytes;
  if (queue.length >= 100 || queueBytes >= 512 * 1024) flushFrontendDiagnostics().catch(() => {});
  else scheduleFlush();
  return true;
}

function serializeConsoleArg(value) {
  if (value instanceof Error) return sanitizeDiagnosticValue(value);
  if (typeof value === 'string') return value;
  return sanitizeDiagnosticValue(value);
}

function installGlobalRuntimeLogging() {
  if (typeof window === 'undefined') return () => {};
  const cleanup = [];
  const on = (target, type, handler, options) => {
    target.addEventListener(type, handler, options);
    cleanup.push(() => target.removeEventListener(type, handler, options));
  };

  const onError = (event) => logFrontendEvent('runtime', 'window-error', {
    message: event.message,
    filename: sanitizeDiagnosticUrl(event.filename || ''),
    lineno: event.lineno,
    colno: event.colno,
    error: event.error,
  }, 'error');
  const onUnhandled = (event) => logFrontendEvent('runtime', 'unhandled-rejection', { reason: event.reason }, 'error');
  on(window, 'error', onError);
  on(window, 'unhandledrejection', onUnhandled);
  on(window, 'online', () => logFrontendEvent('browser', 'online', { online: navigator.onLine }));
  on(window, 'offline', () => logFrontendEvent('browser', 'offline', { online: navigator.onLine }, 'warn'));
  on(window, 'pageshow', (event) => logFrontendEvent('browser', 'pageshow', { persisted: Boolean(event.persisted) }));
  on(window, 'pagehide', (event) => {
    logFrontendEvent('browser', 'pagehide', { persisted: Boolean(event.persisted) });
    flushFrontendDiagnostics().catch(() => {});
  });
  on(document, 'visibilitychange', () => logFrontendEvent('browser', 'visibility', { state: document.visibilityState }));

  const onClick = (event) => logFrontendEvent('interaction', 'click', { target: describeDiagnosticTarget(event.target) });
  const onSubmit = (event) => logFrontendEvent('interaction', 'submit', { target: describeDiagnosticTarget(event.target) });
  const onChange = (event) => {
    const target = event.target;
    const type = String(target?.type || '').toLowerCase();
    logFrontendEvent('interaction', 'change', {
      target: describeDiagnosticTarget(target),
      type: type || undefined,
      checked: type === 'checkbox' || type === 'radio' ? Boolean(target?.checked) : undefined,
    });
  };
  on(document, 'click', onClick, true);
  on(document, 'submit', onSubmit, true);
  on(document, 'change', onChange, true);

  const onKeyDown = (event) => {
    const key = String(event.key || '');
    if (!(event.ctrlKey || event.metaKey || event.altKey || key.length > 1)) return;
    logFrontendEvent('interaction', 'key', { key, ctrl: event.ctrlKey, meta: event.metaKey, alt: event.altKey, shift: event.shiftKey, target: describeDiagnosticTarget(event.target) });
  };
  on(document, 'keydown', onKeyDown, true);

  let scrollTimer = 0;
  const onScroll = () => {
    if (scrollTimer) window.clearTimeout(scrollTimer);
    scrollTimer = window.setTimeout(() => {
      scrollTimer = 0;
      logFrontendEvent('interaction', 'scroll-settled', { x: Math.round(window.scrollX), y: Math.round(window.scrollY) });
    }, 700);
  };
  on(window, 'scroll', onScroll, { passive: true });
  cleanup.push(() => { if (scrollTimer) window.clearTimeout(scrollTimer); });

  let resizeTimer = 0;
  const onResize = () => {
    if (resizeTimer) window.clearTimeout(resizeTimer);
    resizeTimer = window.setTimeout(() => {
      resizeTimer = 0;
      logFrontendEvent('browser', 'resize', { width: window.innerWidth, height: window.innerHeight, dpr: window.devicePixelRatio });
    }, 400);
  };
  on(window, 'resize', onResize, { passive: true });
  cleanup.push(() => { if (resizeTimer) window.clearTimeout(resizeTimer); });

  const originalFetch = typeof window.fetch === 'function' ? window.fetch : null;
  if (originalFetch) {
    const diagnosticsFetch = async (input, init = {}) => {
      const url = typeof input === 'string' ? input : input?.url || '';
      const method = String(init?.method || input?.method || 'GET').toUpperCase();
      const startedAt = Date.now();
      logFrontendEvent('fetch', 'request', { method, url: sanitizeDiagnosticUrl(url) });
      try {
        const response = await originalFetch.call(window, input, init);
        logFrontendEvent('fetch', 'response', { method, url: sanitizeDiagnosticUrl(url), status: response.status, durationMs: Date.now() - startedAt });
        return response;
      } catch (error) {
        logFrontendEvent('fetch', 'error', { method, url: sanitizeDiagnosticUrl(url), durationMs: Date.now() - startedAt, error }, 'error');
        throw error;
      }
    };
    window.fetch = diagnosticsFetch;
    cleanup.push(() => { if (window.fetch === diagnosticsFetch) window.fetch = originalFetch; });
  }

  const originalWarn = console.warn;
  const originalError = console.error;
  console.warn = (...args) => {
    logFrontendEvent('console', 'warn', { args: args.map(serializeConsoleArg) }, 'warn');
    return originalWarn.apply(console, args);
  };
  console.error = (...args) => {
    logFrontendEvent('console', 'error', { args: args.map(serializeConsoleArg) }, 'error');
    return originalError.apply(console, args);
  };
  cleanup.push(() => { console.warn = originalWarn; console.error = originalError; });

  const observers = [];
  if (typeof PerformanceObserver !== 'undefined') {
    try {
      const longTaskObserver = new PerformanceObserver((list) => {
        list.getEntries().forEach((entry) => logFrontendEvent('performance', 'long-task', {
          durationMs: Math.round(entry.duration),
          startMs: Math.round(entry.startTime),
        }, entry.duration >= 200 ? 'warn' : 'info'));
      });
      longTaskObserver.observe({ type: 'longtask', buffered: true });
      observers.push(longTaskObserver);
    } catch {}
    try {
      const resourceObserver = new PerformanceObserver((list) => {
        list.getEntries().forEach((entry) => {
          if (entry.duration < 1000) return;
          logFrontendEvent('performance', 'slow-resource', {
            name: sanitizeDiagnosticUrl(entry.name),
            durationMs: Math.round(entry.duration),
            transferSize: Number(entry.transferSize || 0),
            initiatorType: entry.initiatorType,
          }, 'warn');
        });
      });
      resourceObserver.observe({ type: 'resource', buffered: true });
      observers.push(resourceObserver);
    } catch {}
  }
  cleanup.push(() => observers.forEach((observer) => observer.disconnect()));

  const sampleMemory = () => {
    const memory = performance?.memory;
    logFrontendEvent('performance', 'memory-sample', {
      jsHeapSizeLimit: memory?.jsHeapSizeLimit,
      totalJSHeapSize: memory?.totalJSHeapSize,
      usedJSHeapSize: memory?.usedJSHeapSize,
      deviceMemoryGb: navigator?.deviceMemory,
      domElements: document?.getElementsByTagName?.('*')?.length,
      resourceEntries: performance?.getEntriesByType?.('resource')?.length,
    });
  };
  sampleMemory();
  const memoryTimer = window.setInterval(sampleMemory, MEMORY_SAMPLE_MS);
  cleanup.push(() => window.clearInterval(memoryTimer));

  logFrontendEvent('runtime', 'developer-mode-started', {
    userAgent: navigator.userAgent,
    language: navigator.language,
    online: navigator.onLine,
    viewport: { width: window.innerWidth, height: window.innerHeight, dpr: window.devicePixelRatio },
    limitMb: getFrontendDiagnosticsConfig().limitMb,
  });

  return () => {
    cleanup.reverse().forEach((fn) => { try { fn(); } catch {} });
    flushFrontendDiagnostics().catch(() => {});
  };
}

function syncRuntimeState() {
  const { enabled } = getFrontendDiagnosticsConfig();
  if (enabled && !runtimeStop) runtimeStop = installGlobalRuntimeLogging();
  if (!enabled && runtimeStop) {
    const stop = runtimeStop;
    runtimeStop = null;
    stop();
  }
}

export function startFrontendDiagnostics() {
  if (typeof window === 'undefined') return () => {};
  if (!configListenerInstalled) {
    configListenerInstalled = true;
    window.addEventListener(CONFIG_EVENT, syncRuntimeState);
    window.addEventListener('storage', (event) => {
      if (event.key === ENABLED_KEY || event.key === LIMIT_KEY) {
        refreshCachedConfig();
        syncRuntimeState();
      }
    });
  }
  syncRuntimeState();
  return () => {};
}

export function instrumentSignalRConnection(connection, name, detail = {}) {
  if (!connection || connection.__taskforgeDiagnosticsInstrumented) return connection;
  connection.__taskforgeDiagnosticsInstrumented = true;
  const base = { hub: name, ...sanitizeDiagnosticValue(detail) };
  try { connection.onreconnecting((error) => logFrontendEvent('realtime', 'reconnecting', { ...base, error }, 'warn')); } catch {}
  try { connection.onreconnected((connectionId) => logFrontendEvent('realtime', 'reconnected', { ...base, connectionId })); } catch {}
  try { connection.onclose((error) => logFrontendEvent('realtime', 'closed', { ...base, error }, error ? 'warn' : 'info')); } catch {}
  return connection;
}

export async function getFrontendDiagnosticsStats() {
  const config = getFrontendDiagnosticsConfig();
  if (!isBrowser()) return { ...config, count: 0, bytes: 0, queued: queue.length, droppedInMemory, storageError };
  try {
    await flushFrontendDiagnostics();
    const db = await openDatabase();
    const tx = db.transaction([EVENTS_STORE, META_STORE], 'readonly');
    const done = transactionDone(tx);
    const events = tx.objectStore(EVENTS_STORE);
    const meta = tx.objectStore(META_STORE);
    const [count, total] = await Promise.all([
      requestValue(events.count()),
      requestValue(meta.get('totalBytes')).catch(() => null),
    ]);
    await done;
    return {
      ...config,
      count: Number(count || 0),
      bytes: Number(total?.value || 0),
      queued: queue.length,
      droppedInMemory,
      storageError,
    };
  } catch (error) {
    return { ...config, count: 0, bytes: 0, queued: queue.length, droppedInMemory, storageError: String(error?.message || error || '') };
  }
}

export async function clearFrontendDiagnostics() {
  if (flushPromise) await flushPromise;
  queue = [];
  queueBytes = 0;
  droppedInMemory = 0;
  storageError = '';
  if (!isBrowser()) return;
  if (flushTimer != null) { window.clearTimeout(flushTimer); flushTimer = null; }
  const db = await openDatabase();
  const tx = db.transaction([EVENTS_STORE, META_STORE], 'readwrite');
  const done = transactionDone(tx);
  tx.objectStore(EVENTS_STORE).clear();
  tx.objectStore(META_STORE).put({ key: 'totalBytes', value: 0, updatedAt: Date.now() });
  await done;
}

async function readEventBatch(afterId, limit = 1000) {
  const db = await openDatabase();
  const tx = db.transaction(EVENTS_STORE, 'readonly');
  const done = transactionDone(tx);
  const store = tx.objectStore(EVENTS_STORE);
  const range = afterId == null ? undefined : IDBKeyRange.lowerBound(afterId, true);
  const rows = await requestValue(store.getAll(range, limit));
  await done;
  return Array.isArray(rows) ? rows : [];
}

function triggerDownload(blob, fileName) {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.style.display = 'none';
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 30_000);
}

function exportHeader(stats) {
  return JSON.stringify({
    type: 'taskforge-frontend-diagnostics',
    exportedAt: new Date().toISOString(),
    sessionId: SESSION_ID,
    stats,
    browser: sanitizeDiagnosticValue({ userAgent: navigator.userAgent, language: navigator.language }),
  }) + '\n';
}

export async function downloadFrontendDiagnostics() {
  await flushFrontendDiagnostics();
  const stats = await getFrontendDiagnosticsStats();
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const encoder = new TextEncoder();

  if (typeof CompressionStream !== 'undefined') {
    const compression = new CompressionStream('gzip');
    const writer = compression.writable.getWriter();
    const blobPromise = new Response(compression.readable, { headers: { 'Content-Type': 'application/gzip' } }).blob();
    await writer.write(encoder.encode(exportHeader(stats)));
    let afterId = null;
    while (true) {
      const rows = await readEventBatch(afterId, 1000);
      if (!rows.length) break;
      const text = rows.map((row) => JSON.stringify(row)).join('\n') + '\n';
      await writer.write(encoder.encode(text));
      afterId = rows[rows.length - 1].id;
      if (rows.length < 1000) break;
    }
    await writer.close();
    const blob = await blobPromise;
    triggerDownload(blob, `taskforge-frontend-logs-${stamp}.jsonl.gz`);
    logFrontendEvent('diagnostics', 'logs-downloaded', { count: stats.count, bytes: stats.bytes, compressed: true });
    return { ...stats, fileName: `taskforge-frontend-logs-${stamp}.jsonl.gz`, compressed: true };
  }

  const parts = [new Blob([exportHeader(stats)], { type: 'application/x-ndjson' })];
  let afterId = null;
  while (true) {
    const rows = await readEventBatch(afterId, 1000);
    if (!rows.length) break;
    parts.push(new Blob([rows.map((row) => JSON.stringify(row)).join('\n'), '\n'], { type: 'application/x-ndjson' }));
    afterId = rows[rows.length - 1].id;
    if (rows.length < 1000) break;
  }
  const blob = new Blob(parts, { type: 'application/x-ndjson' });
  triggerDownload(blob, `taskforge-frontend-logs-${stamp}.jsonl`);
  logFrontendEvent('diagnostics', 'logs-downloaded', { count: stats.count, bytes: stats.bytes, compressed: false });
  return { ...stats, fileName: `taskforge-frontend-logs-${stamp}.jsonl`, compressed: false };
}
