import { useEffect, useMemo, useState } from 'react';

export function useProgressiveList(items, options = {}) {
  const {
    enabled = true,
    initialCount = 8,
    step = 4,
    intervalMs = 80,
    resetKey = 'default',
  } = options;

  const safeItems = Array.isArray(items) ? items : [];
  const total = safeItems.length;
  const normalizedInitial = Math.max(1, Number(initialCount) || 1);
  const normalizedStep = Math.max(1, Number(step) || 1);
  const normalizedInterval = Math.max(16, Number(intervalMs) || 80);

  const [visibleCount, setVisibleCount] = useState(() => (
    enabled ? Math.min(normalizedInitial, total) : total
  ));

  useEffect(() => {
    setVisibleCount(enabled ? Math.min(normalizedInitial, total) : total);
  }, [enabled, normalizedInitial, resetKey]);

  useEffect(() => {
    if (!enabled) {
      setVisibleCount(total);
      return undefined;
    }

    if (visibleCount >= total) return undefined;

    const timer = window.setTimeout(() => {
      setVisibleCount((current) => Math.min(current + normalizedStep, total));
    }, normalizedInterval);

    return () => window.clearTimeout(timer);
  }, [enabled, total, visibleCount, normalizedStep, normalizedInterval]);

  useEffect(() => {
    if (visibleCount > total) setVisibleCount(total);
  }, [total, visibleCount]);

  const visibleItems = useMemo(
    () => safeItems.slice(0, Math.min(visibleCount, total)),
    [safeItems, visibleCount, total]
  );

  return {
    visibleItems,
    visibleCount: Math.min(visibleCount, total),
    total,
    isRevealing: enabled && visibleCount < total,
    revealAll: () => setVisibleCount(total),
  };
}
