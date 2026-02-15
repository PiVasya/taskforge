import React, { useEffect, useMemo, useState } from 'react';
import { getMyQuotas } from '../api/quotas';

function fmtSeconds(sec) {
  const s = Math.max(0, Math.floor(sec || 0));
  const m = Math.floor(s / 60);
  const r = s % 60;
  if (m <= 0) return `${r}с`;
  return `${m}м ${r}с`;
}

// bucket: 'tasks' | 'top'
export default function QuotaPill({ bucket = 'tasks', className = '' }) {
  const [data, setData] = useState(null);
  const [tick, setTick] = useState(0);

  const title = bucket === 'top' ? 'Квота топа' : 'Квота заданий';

  const refresh = async () => {
    try {
      const q = await getMyQuotas();
      setData(q || null);
    } catch {
      // silently ignore
    }
  };

  useEffect(() => {
    refresh();

    const onChanged = () => refresh();
    window.addEventListener('quota:changed', onChanged);

    // Чтобы "дотягивать" значения после автопродления токена/первой загрузки
    const id = window.setTimeout(() => refresh(), 800);

    return () => {
      window.removeEventListener('quota:changed', onChanged);
      window.clearTimeout(id);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bucket]);

  // live countdown when empty
  useEffect(() => {
    if (!data) return undefined;

    const section = bucket === 'top' ? data.top : data.tasks;
    const remaining = section?.remaining ?? null;
    if (remaining === null) return undefined;

    const next = section?.nextRefillAtUtc;
    if (!next) return undefined;

    if (remaining > 0) return undefined;

    const i = window.setInterval(() => setTick((x) => x + 1), 1000);
    return () => window.clearInterval(i);
  }, [data, bucket]);

  const view = useMemo(() => {
    if (!data) return null;
    const s = bucket === 'top' ? data.top : data.tasks;
    if (!s) return null;

    const remaining = Number(s.remaining ?? 0);
    const capacity = Number(s.capacity ?? 0);

    let etaText = null;
    const nextAt = s.nextRefillAtUtc ? new Date(s.nextRefillAtUtc).getTime() : null;
    if (remaining <= 0 && nextAt) {
      const sec = Math.ceil((nextAt - Date.now()) / 1000);
      etaText = fmtSeconds(sec);
    }

    return { remaining, capacity, etaText };
  }, [data, bucket, tick]);

  if (!view) return null;

  const isEmpty = view.remaining <= 0;

  return (
    <div
      title={title}
      className={[
        'inline-flex items-center gap-2 rounded-full px-3 py-1 text-xs border',
        'bg-white/50 dark:bg-white/5 backdrop-blur',
        'border-[rgba(var(--border)/0.7)]',
        isEmpty ? 'opacity-80' : '',
        className,
      ].join(' ')}
    >
      <span className="font-medium">{title}:</span>
      <span className="tabular-nums">{view.remaining}/{view.capacity}</span>
      {view.etaText && (
        <span className="opacity-80">({view.etaText})</span>
      )}
    </div>
  );
}
