import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { getMyQuotas } from '../api/quotas';
import useSecondClock from '../hooks/useSecondClock';

const EMPTY_ACTIONS = Object.freeze({
  refresh: async () => null,
});

const TasksQuotaContext = createContext(null);
const TopQuotaContext = createContext(null);
const RawQuotaContext = createContext(null);
const QuotaActionsContext = createContext(EMPTY_ACTIONS);

TasksQuotaContext.displayName = 'TasksQuotaContext';
TopQuotaContext.displayName = 'TopQuotaContext';
RawQuotaContext.displayName = 'RawQuotaContext';
QuotaActionsContext.displayName = 'QuotaActionsContext';

function toIsoFromRetry(retryAfterSeconds) {
  const retry = Number(retryAfterSeconds);
  if (!Number.isFinite(retry) || retry <= 0) return null;
  return new Date(Date.now() + retry * 1000).toISOString();
}

function normalizeBucket(section, bucket) {
  if (!section) return null;
  const remaining = Number(section.remaining ?? 0);
  const capacity = Number(section.capacity ?? 0);
  const retryAfterSeconds = Number(section.retryAfterSeconds ?? 0);
  return {
    bucket,
    remaining: Number.isFinite(remaining) ? remaining : 0,
    capacity: Number.isFinite(capacity) ? capacity : 0,
    retryAfterSeconds: Number.isFinite(retryAfterSeconds) ? retryAfterSeconds : 0,
    nextRefillAtUtc: section.nextRefillAtUtc || toIsoFromRetry(retryAfterSeconds),
    unlimited: Boolean(section.unlimited),
  };
}

function normalizePayload(payload) {
  if (!payload) return null;
  return {
    unified: Boolean(payload.unified),
    unlimited: Boolean(payload.unlimited),
    tasks: normalizeBucket(payload.tasks, 'tasks'),
    top: normalizeBucket(payload.top, 'top'),
  };
}

function sameBucket(left, right) {
  if (left === right) return true;
  if (!left || !right) return false;
  return left.bucket === right.bucket
    && left.remaining === right.remaining
    && left.capacity === right.capacity
    && left.retryAfterSeconds === right.retryAfterSeconds
    && left.nextRefillAtUtc === right.nextRefillAtUtc
    && left.unlimited === right.unlimited;
}

function reconcilePayload(previous, next) {
  if (!next) return null;
  if (!previous) return next;

  const tasks = sameBucket(previous.tasks, next.tasks) ? previous.tasks : next.tasks;
  const top = sameBucket(previous.top, next.top) ? previous.top : next.top;
  if (previous.unified === next.unified
    && previous.unlimited === next.unlimited
    && tasks === previous.tasks
    && top === previous.top) {
    return previous;
  }
  return { unified: next.unified, unlimited: next.unlimited, tasks, top };
}

function mergeBucket(previous, patch) {
  const base = previous || {
    bucket: patch.bucket,
    remaining: 0,
    capacity: 0,
    retryAfterSeconds: 0,
    nextRefillAtUtc: null,
    unlimited: false,
  };
  const remaining = patch.remaining == null ? base.remaining : Number(patch.remaining);
  const capacity = patch.capacity == null ? base.capacity : Number(patch.capacity);
  const retryAfterSeconds = patch.retryAfterSeconds == null
    ? base.retryAfterSeconds
    : Number(patch.retryAfterSeconds);
  const normalizedRemaining = Number.isFinite(remaining) ? remaining : base.remaining;
  const normalizedCapacity = Number.isFinite(capacity) ? capacity : base.capacity;
  const isFull = normalizedCapacity > 0 && normalizedRemaining >= normalizedCapacity;

  const next = {
    bucket: patch.bucket || base.bucket,
    remaining: normalizedRemaining,
    capacity: normalizedCapacity,
    retryAfterSeconds: Number.isFinite(retryAfterSeconds) ? retryAfterSeconds : 0,
    nextRefillAtUtc: isFull
      ? null
      : (
          patch.nextRefillAtUtc
          || toIsoFromRetry(patch.retryAfterSeconds)
          || base.nextRefillAtUtc
          || null
        ),
    unlimited: patch.unlimited == null ? Boolean(base.unlimited) : Boolean(patch.unlimited),
  };

  return sameBucket(base, next) ? base : next;
}

function mergeQuotaUpdate(previous, detail) {
  const bucket = String(detail?.bucket || '').trim().toLowerCase();
  if (bucket !== 'tasks' && bucket !== 'top') return previous;
  const base = previous || { unified: false, unlimited: false, tasks: null, top: null };
  const nextBucket = mergeBucket(base[bucket], {
    bucket,
    remaining: detail?.remaining,
    capacity: detail?.capacity,
    retryAfterSeconds: detail?.retryAfterSeconds,
    nextRefillAtUtc: detail?.nextRefillAtUtc,
    unlimited: detail?.unlimited,
  });
  if (nextBucket === base[bucket]) return base;
  return { ...base, [bucket]: nextBucket };
}

function getSoonestRefreshAt(data) {
  if (!data) return null;
  return ['tasks', 'top']
    .map((key) => data[key])
    .filter(Boolean)
    .filter((bucket) => !bucket.unlimited)
    .filter((bucket) => Number(bucket.remaining) < Number(bucket.capacity))
    .map((bucket) => (bucket.nextRefillAtUtc ? new Date(bucket.nextRefillAtUtc).getTime() : null))
    .filter((value) => Number.isFinite(value) && value > Date.now())
    .sort((left, right) => left - right)[0] ?? null;
}

function buildBucketView(bucket, nowMs) {
  if (!bucket) return null;
  const remaining = Number(bucket.remaining ?? 0);
  const capacity = Number(bucket.capacity ?? 0);
  const nextAtMs = bucket.nextRefillAtUtc ? new Date(bucket.nextRefillAtUtc).getTime() : null;
  const etaSeconds = Number.isFinite(nextAtMs)
    ? Math.max(0, Math.ceil((nextAtMs - nowMs) / 1000))
    : 0;
  const unlimited = Boolean(bucket.unlimited);
  return {
    ...bucket,
    remaining,
    capacity,
    unlimited,
    etaSeconds: unlimited ? 0 : etaSeconds,
    isEmpty: unlimited ? false : remaining <= 0,
    isFull: unlimited || (capacity > 0 && remaining >= capacity),
  };
}

export function QuotaProvider({ enabled = true, children }) {
  const [data, setData] = useState(null);
  const refreshTimeoutRef = useRef(null);
  const inFlightRef = useRef(null);
  const lastRefreshAtRef = useRef(0);
  const enabledRef = useRef(enabled);
  const sessionGenerationRef = useRef(0);

  enabledRef.current = enabled;

  const refresh = useCallback(async () => {
    if (!enabled) return null;
    if (inFlightRef.current) return inFlightRef.current;
    const generation = sessionGenerationRef.current;

    const request = getMyQuotas()
      .then((payload) => {
        if (!enabledRef.current || generation !== sessionGenerationRef.current) {
          return null;
        }
        const normalized = normalizePayload(payload);
        setData((previous) => reconcilePayload(previous, normalized));
        lastRefreshAtRef.current = Date.now();
        return normalized;
      })
      .catch(() => null)
      .finally(() => {
        if (inFlightRef.current === request) inFlightRef.current = null;
      });

    inFlightRef.current = request;
    return request;
  }, [enabled]);

  useEffect(() => {
    if (!enabled) {
      sessionGenerationRef.current += 1;
      setData((previous) => (previous == null ? previous : null));
      inFlightRef.current = null;
      return;
    }
    refresh();
  }, [enabled, refresh]);

  useEffect(() => {
    if (!enabled || typeof window === 'undefined') return undefined;

    const onQuotaChanged = () => {
      window.clearTimeout(refreshTimeoutRef.current);
      refreshTimeoutRef.current = window.setTimeout(refresh, 120);
    };
    const onQuotaUpdate = (event) => {
      setData((previous) => mergeQuotaUpdate(previous, event?.detail || {}));
    };

    window.addEventListener('quota:changed', onQuotaChanged);
    window.addEventListener('quota:update', onQuotaUpdate);
    return () => {
      window.removeEventListener('quota:changed', onQuotaChanged);
      window.removeEventListener('quota:update', onQuotaUpdate);
      window.clearTimeout(refreshTimeoutRef.current);
    };
  }, [enabled, refresh]);

  useEffect(() => {
    if (!enabled || typeof window === 'undefined') return undefined;

    const refreshAfterPause = () => {
      if (Date.now() - lastRefreshAtRef.current >= 15000) refresh();
    };
    const onVisibility = () => {
      if (!document.hidden) refreshAfterPause();
    };

    window.addEventListener('focus', refreshAfterPause);
    window.addEventListener('online', refreshAfterPause);
    window.addEventListener('pageshow', refreshAfterPause);
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      window.removeEventListener('focus', refreshAfterPause);
      window.removeEventListener('online', refreshAfterPause);
      window.removeEventListener('pageshow', refreshAfterPause);
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [enabled, refresh]);

  useEffect(() => {
    if (!enabled || typeof window === 'undefined') return undefined;
    const refreshAt = getSoonestRefreshAt(data);
    if (!refreshAt) return undefined;

    let cancelled = false;
    let retryId = null;
    const refreshAfterRefill = async () => {
      const result = await refresh();
      if (!result && !cancelled) {
        retryId = window.setTimeout(refreshAfterRefill, 5000);
      }
    };
    const id = window.setTimeout(
      refreshAfterRefill,
      Math.max(250, refreshAt - Date.now() + 250),
    );
    return () => {
      cancelled = true;
      window.clearTimeout(id);
      if (retryId != null) window.clearTimeout(retryId);
    };
  }, [data, enabled, refresh]);

  const actionsValue = useMemo(() => ({ refresh }), [refresh]);

  return (
    <QuotaActionsContext.Provider value={actionsValue}>
      <RawQuotaContext.Provider value={data}>
        <TasksQuotaContext.Provider value={data?.tasks || null}>
          <TopQuotaContext.Provider value={data?.top || null}>
            {children}
          </TopQuotaContext.Provider>
        </TasksQuotaContext.Provider>
      </RawQuotaContext.Provider>
    </QuotaActionsContext.Provider>
  );
}

export function useQuota() {
  const raw = useContext(RawQuotaContext);
  const tasks = useContext(TasksQuotaContext);
  const top = useContext(TopQuotaContext);
  const { refresh } = useContext(QuotaActionsContext);
  return useMemo(() => ({ raw, tasks, top, refresh }), [raw, refresh, tasks, top]);
}

export function useQuotaBucket(bucketName = 'tasks') {
  const bucket = useContext(bucketName === 'top' ? TopQuotaContext : TasksQuotaContext);
  const isFull = bucket && (bucket.unlimited || (Number(bucket.capacity) > 0 && Number(bucket.remaining) >= Number(bucket.capacity)));
  const hasCountdown = Boolean(bucket?.nextRefillAtUtc && !bucket?.unlimited && !isFull);
  const clockNowMs = useSecondClock(hasCountdown);
  const nowMs = hasCountdown ? clockNowMs : Date.now();

  return useMemo(() => buildBucketView(bucket, nowMs), [bucket, nowMs]);
}
