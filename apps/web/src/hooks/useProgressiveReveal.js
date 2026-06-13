import { useEffect, useMemo, useState } from 'react';

export default function useProgressiveReveal(items, options = {}) {
  const {
    initialCount = 8,
    step = 4,
    intervalMs = 70,
    resetKey = '',
    enabled = true,
  } = options;

  const source = Array.isArray(items) ? items : [];
  const safeInitial = Math.max(1, initialCount);
  const safeStep = Math.max(1, step);
  const [visibleCount, setVisibleCount] = useState(() => Math.min(source.length, safeInitial));

  useEffect(() => {
    setVisibleCount(Math.min(source.length, safeInitial));
  }, [resetKey, safeInitial]);

  useEffect(() => {
    setVisibleCount((current) => Math.min(Math.max(current, Math.min(source.length, safeInitial)), source.length));
  }, [source.length, safeInitial]);

  useEffect(() => {
    if (!enabled || visibleCount >= source.length) return undefined;
    const timer = window.setTimeout(() => {
      setVisibleCount((current) => Math.min(source.length, current + safeStep));
    }, Math.max(16, intervalMs));
    return () => window.clearTimeout(timer);
  }, [enabled, source.length, visibleCount, safeStep, intervalMs]);

  const visibleItems = useMemo(() => source.slice(0, visibleCount), [source, visibleCount]);

  return {
    visibleItems,
    visibleCount,
    isRevealing: enabled && visibleCount < source.length,
  };
}
