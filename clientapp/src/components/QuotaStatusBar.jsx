import React from 'react';
import { Zap, Trophy } from 'lucide-react';
import { useQuota } from '../contexts/QuotaContext';

function fmtEta(sec) {
  const total = Math.max(0, Math.ceil(Number(sec) || 0));
  const m = Math.floor(total / 60);
  const s = total % 60;
  if (m <= 0) return `${s}с`;
  return `${m}м ${String(s).padStart(2, '0')}с`;
}

function BucketChip({ icon: Icon, label, bucket }) {
  if (!bucket) return null;

  const meta = bucket.isFull
    ? 'полный'
    : bucket.isEmpty
      ? `ждать ${fmtEta(bucket.etaSeconds)}`
      : `+1 через ${fmtEta(bucket.etaSeconds)}`;

  return (
    <div className="inline-flex min-w-0 items-center gap-3 rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))]/80 px-3 py-2 shadow-soft backdrop-blur">
      <div className="h-9 w-9 shrink-0 rounded-xl grid place-items-center bg-brand-600/12 text-brand-700 dark:text-brand-300">
        <Icon size={17} />
      </div>
      <div className="min-w-0">
        <div className="flex items-center gap-2 text-sm font-semibold leading-tight">
          <span className="truncate">{label}</span>
          <span className="tabular-nums text-brand-700 dark:text-brand-300">{bucket.remaining}/{bucket.capacity}</span>
        </div>
        <div className="text-xs text-neutral-500 dark:text-neutral-400 truncate">{meta}</div>
      </div>
    </div>
  );
}

export default function QuotaStatusBar({ className = '' }) {
  const { tasks, top } = useQuota();

  if (!tasks && !top) return null;

  return (
    <div className={['hidden md:flex flex-wrap items-center justify-center gap-2', className].join(' ')}>
      <BucketChip icon={Zap} label="Задания" bucket={tasks} />
      <BucketChip icon={Trophy} label="Топ" bucket={top} />
    </div>
  );
}
