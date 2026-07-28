import React from 'react';
import { Trophy, Zap } from 'lucide-react';
import { useQuotaBucket } from '../contexts/QuotaContext';

function fmtSeconds(sec) {
  const total = Math.max(0, Math.ceil(Number(sec) || 0));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  return `${m}:${String(s).padStart(2, '0')}`;
}

function QuotaPill({ bucket = 'tasks', className = '' }) {
  const view = useQuotaBucket(bucket === 'top' ? 'top' : 'tasks');

  if (!view) return null;

  const isEmpty = !view.unlimited && view.remaining <= 0;
  const isFull = view.unlimited || (view.capacity > 0 && view.remaining >= view.capacity);
  const Icon = bucket === 'top' ? Trophy : Zap;
  const title = bucket === 'top' ? 'Рейтинг' : 'Задачи';
  const etaText = view.unlimited || isFull ? null : fmtSeconds(view.etaSeconds);
  const stateText = view.unlimited ? 'без ограничений' : isFull ? 'заряд полный' : `+1 через ${etaText}`;
  const valueText = view.unlimited ? '∞' : `${view.remaining}/${view.capacity}`;

  return (
    <div
      title={`${title}: ${valueText}. ${stateText}`}
      aria-label={`${title}: ${valueText}. ${stateText}`}
      className={[
        'inline-flex min-w-0 items-center gap-2 rounded-full px-3 py-1.5 text-xs border',
        'bg-white/50 dark:bg-white/5 backdrop-blur',
        'border-[rgba(var(--border)/0.7)]',
        isEmpty ? 'opacity-85' : '',
        className,
      ].join(' ')}
    >
      <Icon size={14} className="shrink-0" />
      <span className="tabular-nums shrink-0 font-medium">{valueText}</span>
      {etaText && <span className="tabular-nums opacity-75 shrink-0">+1 через {etaText}</span>}
    </div>
  );
}

export default React.memo(QuotaPill);
