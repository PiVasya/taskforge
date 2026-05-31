import React from 'react';
import { Zap, Trophy } from 'lucide-react';
import { useQuota } from '../contexts/QuotaContext';

function fmtEta(sec) {
  const total = Math.max(0, Math.ceil(Number(sec) || 0));
  const m = Math.floor(total / 60);
  const s = total % 60;
  if (m <= 0) return `0:${String(s).padStart(2, '0')}`;
  return `${m}:${String(s).padStart(2, '0')}`;
}

function BucketChip({ icon: Icon, title, bucket, compact = false }) {
  if (!bucket) return null;

  const etaText = bucket.isFull ? null : fmtEta(bucket.etaSeconds);
  const fullTitle = `${title}: ${bucket.remaining}/${bucket.capacity}${etaText ? ` • ${etaText}` : ''}`;

  return (
    <div
      title={fullTitle}
      className={[
        'inline-flex min-w-0 items-center rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))]/80 shadow-soft backdrop-blur',
        compact ? 'gap-1.5 px-2.5 py-1.5' : 'gap-2 px-3 py-2',
      ].join(' ')}
    >
      <div className={[
        'shrink-0 rounded-xl grid place-items-center bg-brand-600/12 text-brand-700 dark:text-brand-300',
        compact ? 'h-7 w-7' : 'h-8 w-8',
      ].join(' ')}><Icon size={compact ? 14 : 16} /></div>
      <div className={[
        'flex min-w-0 items-baseline',
        compact ? 'gap-1.5' : 'gap-2',
      ].join(' ')}><span className={[
          'tabular-nums font-semibold text-neutral-900 dark:text-neutral-100',
          compact ? 'text-[0.92rem]' : 'text-sm',
        ].join(' ')}>
          {bucket.remaining}/{bucket.capacity}
        </span>
        {etaText && (
          <span className={[
            'tabular-nums text-neutral-500 dark:text-neutral-400 shrink-0',
            compact ? 'text-[11px]' : 'text-xs',
          ].join(' ')}>
            {etaText}
          </span>
        )}
      </div>
    </div>
  );
}

export default function QuotaStatusBar({ className = '', compact = false }) {
  const { tasks, top } = useQuota();

  if (!tasks && !top) return null;

  return (
    <div className={[compact ? 'flex items-center gap-2 shrink-0' : 'hidden md:flex flex-wrap items-center justify-center gap-2', className].join(' ')}>
      <BucketChip icon={Zap} title="Энергия" bucket={tasks} compact={compact} />
      <BucketChip icon={Trophy} title="Топ" bucket={top} compact={compact} />
    </div>
  );
}
