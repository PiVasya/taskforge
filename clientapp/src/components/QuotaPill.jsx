import React from 'react';
import { Trophy, Zap } from 'lucide-react';
import { useQuota } from '../contexts/QuotaContext';

function fmtSeconds(sec) {
  const total = Math.max(0, Math.ceil(Number(sec) || 0));
  const m = Math.floor(total / 60);
  const s = total % 60;
  if (m <= 0) return `0:${String(s).padStart(2, '0')}`;
  return `${m}:${String(s).padStart(2, '0')}`;
}

// bucket: 'tasks' | 'top'
export default function QuotaPill({ bucket = 'tasks', className = '' }) {
  const quota = useQuota();
  const view = bucket === 'top' ? quota.top : quota.tasks;

  if (!view) return null;

  const isEmpty = view.remaining <= 0;
  const isFull = view.capacity > 0 && view.remaining >= view.capacity;
  const Icon = bucket === 'top' ? Trophy : Zap;
  const title = bucket === 'top' ? 'Топ' : 'Энергия';
  const etaText = isFull ? null : fmtSeconds(view.etaSeconds);

  return (
    <div
      title={`${title}: ${view.remaining}/${view.capacity}${etaText ? ` • ${etaText}` : ''}`}
      className={[
        'inline-flex min-w-0 items-center gap-2 rounded-full px-3 py-1.5 text-xs border',
        'bg-white/50 dark:bg-white/5 backdrop-blur',
        'border-[rgba(var(--border)/0.7)]',
        isEmpty ? 'opacity-85' : '',
        className,
      ].join(' ')}
    >
      <Icon size={14} className="shrink-0" />
      <span className="tabular-nums shrink-0 font-medium">{view.remaining}/{view.capacity}</span>
      {etaText && <span className="tabular-nums opacity-75 shrink-0">{etaText}</span>}
    </div>
  );
}
