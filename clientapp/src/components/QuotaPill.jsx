import React from 'react';
import { useQuota } from '../contexts/QuotaContext';

function fmtSeconds(sec) {
  const total = Math.max(0, Math.ceil(Number(sec) || 0));
  const m = Math.floor(total / 60);
  const s = total % 60;
  if (m <= 0) return `${s}с`;
  return `${m}м ${String(s).padStart(2, '0')}с`;
}

// bucket: 'tasks' | 'top'
export default function QuotaPill({ bucket = 'tasks', className = '' }) {
  const quota = useQuota();
  const view = bucket === 'top' ? quota.top : quota.tasks;

  if (!view) return null;

  const title = bucket === 'top' ? 'Квота топа' : 'Квота заданий';
  const isEmpty = view.remaining <= 0;
  const isFull = view.capacity > 0 && view.remaining >= view.capacity;

  let etaText = null;
  if (!isFull) {
    etaText = isEmpty ? `ждать ${fmtSeconds(view.etaSeconds)}` : `+1 через ${fmtSeconds(view.etaSeconds)}`;
  }

  return (
    <div
      title={title}
      className={[
        'inline-flex min-w-0 items-center gap-2 rounded-full px-3 py-1.5 text-xs border',
        'bg-white/50 dark:bg-white/5 backdrop-blur',
        'border-[rgba(var(--border)/0.7)]',
        isEmpty ? 'opacity-85' : '',
        className,
      ].join(' ')}
    >
      <span className="font-medium shrink-0">{title}:</span>
      <span className="tabular-nums shrink-0">{view.remaining}/{view.capacity}</span>
      {etaText && <span className="opacity-80 truncate">({etaText})</span>}
    </div>
  );
}
