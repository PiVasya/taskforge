import React, { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { getMyQuotas } from '../api/quotas';

const QuotaContext = createContext(null);

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
  const nextRefillAtUtc = section.nextRefillAtUtc || toIsoFromRetry(retryAfterSeconds);

  return {
    bucket,
    remaining: Number.isFinite(remaining) ? remaining : 0,
    capacity: Number.isFinite(capacity) ? capacity : 0,
    retryAfterSeconds: Number.isFinite(retryAfterSeconds) ? retryAfterSeconds : 0,
    nextRefillAtUtc: nextRefillAtUtc || null,
  };
}

function normalizePayload(payload) {
  if (!payload) return null;
  return {
    unified: Boolean(payload.unified),
    tasks: normalizeBucket(payload.tasks, 'tasks'),
    top: normalizeBucket(payload.top, 'top'),
  };
}

function mergeBucket(prevBucket, patch) {
  const prev = prevBucket || { bucket: patch.bucket, remaining: 0, capacity: 0, retryAfterSeconds: 0, nextRefillAtUtc: null };
  const remaining = patch.remaining == null ? prev.remaining : Number(patch.remaining);
  const capacity = patch.capacity == null ? prev.capacity : Number(patch.capacity);

  const explicitRetry = patch.retryAfterSeconds == null ? prev.retryAfterSeconds : Number(patch.retryAfterSeconds);
  const nextRefillAtUtc = patch.nextRefillAtUtc || toIsoFromRetry(patch.retryAfterSeconds) || prev.nextRefillAtUtc || null;

  return {
    bucket: patch.bucket || prev.bucket,
    remaining: Number.isFinite(remaining) ? remaining : prev.remaining,
    capacity: Number.isFinite(capacity) ? capacity : prev.capacity,
    retryAfterSeconds: Number.isFinite(explicitRetry) ? explicitRetry : 0,
    nextRefillAtUtc,
  };
}

function mergeQuotaUpdate(prevData, detail) {
  const bucket = String(detail?.bucket || '').trim().toLowerCase();
  if (!bucket || (bucket !== 'tasks' && bucket !== 'top')) return prevData;

  const base = prevData || { unified: false, tasks: null, top: null };
  const patch = {
    bucket,
    remaining: detail?.remaining,
    capacity: detail?.capacity,
    retryAfterSeconds: detail?.retryAfterSeconds,
    nextRefillAtUtc: detail?.nextRefillAtUtc,
  };

  return {
    ...base,
    [bucket]: mergeBucket(base[bucket], patch),
  };
}

function getSoonestRefreshAt(data) {
  if (!data) return null;

  return ['tasks', 'top']
    .map((key) => data[key])
    .filter(Boolean)
    .filter((bucket) => Number(bucket.remaining) < Number(bucket.capacity))
    .map((bucket) => (bucket.nextRefillAtUtc ? new Date(bucket.nextRefillAtUtc).getTime() : null))
    .filter((ts) => Number.isFinite(ts) && ts > Date.now())
    .sort((a, b) => a - b)[0] ?? null;
}

function buildBucketView(bucket, nowMs) {
  if (!bucket) return null;

  const remaining = Number(bucket.remaining ?? 0);
  const capacity = Number(bucket.capacity ?? 0);
  const nextAtMs = bucket.nextRefillAtUtc ? new Date(bucket.nextRefillAtUtc).getTime() : null;
  const etaSeconds = Number.isFinite(nextAtMs) ? Math.max(0, Math.ceil((nextAtMs - nowMs) / 1000)) : 0;
  const isEmpty = remaining <= 0;
  const isFull = capacity > 0 && remaining >= capacity;

  return {
    ...bucket,
    remaining,
    capacity,
    etaSeconds,
    isEmpty,
    isFull,
  };
}

export function QuotaProvider({ enabled = true, children }) {
  const [data, setData] = useState(null);
  const [tick, setTick] = useState(Date.now());
  const refreshTimeoutRef = useRef(null);

  const refresh = useCallback(async () => {
    if (!enabled) {
      setData(null);
      return;
    }

    try {
      const payload = await getMyQuotas();
      setData(normalizePayload(payload));
    } catch {
      
    }
  }, [enabled]);

  useEffect(() => {
    refresh();
  }, [refresh]);

  useEffect(() => {
    if (!enabled || typeof window === 'undefined') return undefined;

    const onQuotaChanged = () => {
      window.clearTimeout(refreshTimeoutRef.current);
      refreshTimeoutRef.current = window.setTimeout(() => {
        refresh();
      }, 120);
    };

    const onQuotaUpdate = (event) => {
      setData((prev) => mergeQuotaUpdate(prev, event?.detail || {}));
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
    const id = window.setInterval(() => setTick(Date.now()), 1000);
    return () => window.clearInterval(id);
  }, [enabled]);

  useEffect(() => {
    if (!enabled || typeof window === 'undefined') return undefined;

    const at = getSoonestRefreshAt(data);
    if (!at) return undefined;

    const delay = Math.max(250, at - Date.now() + 250);
    const id = window.setTimeout(() => refresh(), delay);
    return () => window.clearTimeout(id);
  }, [data, enabled, refresh]);

  const value = useMemo(() => {
    const nowMs = tick || Date.now();
    return {
      refresh,
      raw: data,
      tasks: buildBucketView(data?.tasks, nowMs),
      top: buildBucketView(data?.top, nowMs),
    };
  }, [data, refresh, tick]);

  return <QuotaContext.Provider value={value}>{children}</QuotaContext.Provider>;
}

export function useQuota() {
  return useContext(QuotaContext) || { refresh: async () => {}, raw: null, tasks: null, top: null };
}
