const IDLE_SNAPSHOT = Object.freeze({
  data: undefined,
  error: null,
  status: 'idle',
  fetchStatus: 'idle',
  updatedAt: 0,
  isInvalidated: false,
});

function normalizeKey(queryKey) {
  if (Array.isArray(queryKey)) return queryKey;
  return [queryKey];
}

function serializePart(value) {
  if (value === undefined) return 'undefined';
  if (value === null) return 'null';
  if (typeof value === 'string') return JSON.stringify(value);
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  if (value instanceof Date) return `date:${value.toISOString()}`;
  if (Array.isArray(value)) return `[${value.map(serializePart).join(',')}]`;
  if (typeof value === 'object') {
    return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${serializePart(value[key])}`).join(',')}}`;
  }
  return JSON.stringify(String(value));
}

export function hashQueryKey(queryKey) {
  return normalizeKey(queryKey).map(serializePart).join('|');
}

function keyStartsWith(key, prefix) {
  const normalizedKey = normalizeKey(key);
  const normalizedPrefix = normalizeKey(prefix);
  if (normalizedPrefix.length > normalizedKey.length) return false;
  return normalizedPrefix.every((part, index) => serializePart(part) === serializePart(normalizedKey[index]));
}

function createEntry(queryKey, hash) {
  return {
    queryKey: normalizeKey(queryKey),
    hash,
    data: undefined,
    error: null,
    status: 'idle',
    fetchStatus: 'idle',
    updatedAt: 0,
    isInvalidated: false,
    promise: null,
    abortController: null,
    queryFn: null,
    staleTime: 0,
    retry: 1,
    retryDelay: 500,
    listeners: new Set(),
    observerCount: 0,
    gcTimer: null,
    snapshot: IDLE_SNAPSHOT,
  };
}

function updateSnapshot(entry) {
  entry.snapshot = Object.freeze({
    data: entry.data,
    error: entry.error,
    status: entry.status,
    fetchStatus: entry.fetchStatus,
    updatedAt: entry.updatedAt,
    isInvalidated: entry.isInvalidated,
  });
  entry.listeners.forEach((listener) => listener());
}

export class QueryClient {
  constructor({ defaultStaleTime = 30_000, defaultGcTime = 5 * 60_000 } = {}) {
    this.defaultStaleTime = defaultStaleTime;
    this.defaultGcTime = defaultGcTime;
    this.entries = new Map();
  }

  getEntry(queryKey) {
    const hash = hashQueryKey(queryKey);
    let entry = this.entries.get(hash);
    if (!entry) {
      entry = createEntry(queryKey, hash);
      this.entries.set(hash, entry);
    }
    return entry;
  }

  getQueryData(queryKey) {
    return this.entries.get(hashQueryKey(queryKey))?.data;
  }

  getQueryState(queryKey) {
    return this.entries.get(hashQueryKey(queryKey))?.snapshot || IDLE_SNAPSHOT;
  }

  setQueryData(queryKey, updater) {
    const entry = this.getEntry(queryKey);
    const next = typeof updater === 'function' ? updater(entry.data) : updater;
    if (Object.is(next, entry.data) && entry.status === 'success') return entry.data;
    entry.data = next;
    entry.error = null;
    entry.status = 'success';
    entry.fetchStatus = 'idle';
    entry.updatedAt = Date.now();
    entry.isInvalidated = false;
    updateSnapshot(entry);
    return next;
  }

  setQueryError(queryKey, error) {
    const entry = this.getEntry(queryKey);
    entry.error = error;
    entry.status = 'error';
    entry.fetchStatus = 'idle';
    entry.isInvalidated = false;
    updateSnapshot(entry);
  }

  isStale(entry, staleTime = this.defaultStaleTime) {
    if (!entry || entry.status !== 'success') return true;
    if (entry.isInvalidated) return true;
    if (staleTime === Infinity) return false;
    return Date.now() - entry.updatedAt >= Math.max(0, Number(staleTime) || 0);
  }

  async fetchQuery({
    queryKey,
    queryFn,
    staleTime = this.defaultStaleTime,
    force = false,
    retry = 1,
    retryDelay = 500,
  }) {
    const entry = this.getEntry(queryKey);
    if (!force && !this.isStale(entry, staleTime)) return entry.data;
    if (entry.promise) return entry.promise;

    entry.abortController?.abort();
    const controller = new AbortController();
    entry.abortController = controller;
    entry.queryFn = queryFn;
    entry.staleTime = staleTime;
    entry.retry = retry;
    entry.retryDelay = retryDelay;
    entry.fetchStatus = 'fetching';
    if (entry.status === 'idle') entry.status = 'pending';
    entry.error = null;
    updateSnapshot(entry);

    const waitForRetry = (delay) => new Promise((resolve) => {
      const timer = setTimeout(resolve, Math.max(0, Number(delay) || 0));
      controller.signal.addEventListener('abort', () => {
        clearTimeout(timer);
        resolve();
      }, { once: true });
    });

    const settleAborted = () => {
      entry.fetchStatus = 'idle';
      if (entry.status === 'pending' && entry.data === undefined) entry.status = 'idle';
      updateSnapshot(entry);
      return entry.data;
    };

    const canRetry = (error, attempt) => {
      const limit = typeof retry === 'function' ? retry(attempt, error) : attempt <= Math.max(0, Number(retry) || 0);
      if (!limit || controller.signal.aborted) return false;
      const status = Number(error?.response?.status || error?.status || 0);
      return status === 0 || status === 408 || status === 429 || status >= 500;
    };

    let promise;
    promise = (async () => {
      try {
        let attempt = 0;
        let data;
        while (true) {
          try {
            data = await queryFn({ signal: controller.signal, queryKey: entry.queryKey });
            break;
          } catch (error) {
            attempt += 1;
            if (!canRetry(error, attempt)) throw error;
            const delay = typeof retryDelay === 'function' ? retryDelay(attempt, error) : retryDelay * attempt;
            await waitForRetry(delay);
            if (controller.signal.aborted) return settleAborted();
          }
        }

        if (controller.signal.aborted) return settleAborted();
        entry.data = data;
        entry.error = null;
        entry.status = 'success';
        entry.fetchStatus = 'idle';
        entry.updatedAt = Date.now();
        entry.isInvalidated = false;
        updateSnapshot(entry);
        return data;
      } catch (error) {
        if (
          controller.signal.aborted
          || error?.name === 'CanceledError'
          || error?.name === 'AbortError'
          || error?.code === 'ERR_CANCELED'
        ) {
          return settleAborted();
        }
        entry.error = error;
        entry.status = 'error';
        entry.fetchStatus = 'idle';
        entry.isInvalidated = false;
        updateSnapshot(entry);
        throw error;
      } finally {
        if (entry.promise === promise) entry.promise = null;
        if (entry.abortController === controller) entry.abortController = null;
      }
    })();

    entry.promise = promise;
    return promise;
  }

  prefetchQuery(options) {
    return this.fetchQuery(options).catch(() => undefined);
  }

  cancelQueries({ queryKey } = {}) {
    for (const entry of this.entries.values()) {
      if (queryKey == null || keyStartsWith(entry.queryKey, queryKey)) {
        entry.abortController?.abort();
        if (entry.fetchStatus === 'fetching') {
          entry.fetchStatus = 'idle';
          if (entry.status === 'pending' && entry.data === undefined) entry.status = 'idle';
          updateSnapshot(entry);
        }
      }
    }
  }

  invalidateQueries({ queryKey, refetch = true } = {}) {
    const tasks = [];
    for (const entry of this.entries.values()) {
      if (queryKey == null || keyStartsWith(entry.queryKey, queryKey)) {
        entry.isInvalidated = true;
        updateSnapshot(entry);
        if (refetch && entry.queryFn && entry.observerCount > 0) {
          tasks.push(this.fetchQuery({
            queryKey: entry.queryKey,
            queryFn: entry.queryFn,
            staleTime: entry.staleTime,
            retry: entry.retry,
            retryDelay: entry.retryDelay,
            force: true,
          }).catch(() => undefined));
        }
      }
    }
    return Promise.all(tasks);
  }

  removeQueries({ queryKey } = {}) {
    for (const [hash, entry] of this.entries.entries()) {
      if (queryKey == null || keyStartsWith(entry.queryKey, queryKey)) {
        entry.abortController?.abort();
        if (entry.gcTimer) clearTimeout(entry.gcTimer);
        this.entries.delete(hash);
      }
    }
  }

  clear() {
    this.removeQueries();
  }

  subscribe(queryKey, listener, gcTime = this.defaultGcTime) {
    const entry = this.getEntry(queryKey);
    if (entry.gcTimer) {
      clearTimeout(entry.gcTimer);
      entry.gcTimer = null;
    }
    entry.listeners.add(listener);
    entry.observerCount += 1;

    return () => {
      entry.listeners.delete(listener);
      entry.observerCount = Math.max(0, entry.observerCount - 1);
      if (entry.observerCount === 0) {
        // A route/key switch must not leave an obsolete HTTP request running.
        // Cached successful data is retained until gcTime, but an in-flight
        // request with no observers is cancelled immediately.
        entry.abortController?.abort();
      }
      if (entry.observerCount === 0 && Number.isFinite(gcTime) && gcTime >= 0) {
        entry.gcTimer = setTimeout(() => {
          if (entry.observerCount === 0) {
            entry.abortController?.abort();
            this.entries.delete(entry.hash);
          }
        }, gcTime);
      }
    };
  }
}

export const defaultQueryClient = new QueryClient();
export { IDLE_SNAPSHOT };
