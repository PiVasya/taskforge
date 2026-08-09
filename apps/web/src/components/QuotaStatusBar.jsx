import React from 'react';
import { Zap, Trophy } from 'lucide-react';
import { useQuotaBucket } from '../contexts/QuotaContext';

function fmtEta(sec) {
  const total = Math.max(0, Math.ceil(Number(sec) || 0));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  return `${m}:${String(s).padStart(2, '0')}`;
}

function BucketChip({ icon: Icon, title, bucket, compact = false, mobile = false }) {
  if (!bucket) return null;

  const etaText = bucket.unlimited || bucket.isFull ? null : fmtEta(bucket.etaSeconds);
  const stateText = bucket.unlimited
    ? 'без ограничений'
    : bucket.isFull
      ? 'заряд полный'
      : `+1 через ${etaText}`;
  const valueText = bucket.unlimited ? '∞' : `${bucket.remaining}/${bucket.capacity}`;
  const fullTitle = `${title}: ${valueText}. ${stateText}`;

  return (
    <div
      title={fullTitle}
      aria-label={fullTitle}
      className={[
        'quota-chip inline-flex min-w-0 items-center rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))]/90 shadow-soft backdrop-blur',
        compact ? 'quota-chip--compact gap-1.5 px-2.5 py-1.5' : 'gap-2 px-3 py-2',
        mobile ? 'quota-chip--mobile flex-1' : '',
        bucket.isEmpty ? 'quota-chip--empty' : '',
      ].join(' ')}
    >
      <div className={[
        'quota-chip__icon shrink-0 rounded-xl grid place-items-center bg-brand-600/12 text-brand-700 dark:text-brand-300',
        compact ? 'h-7 w-7' : 'h-8 w-8',
      ].join(' ')}><Icon size={compact ? 14 : 16} /></div>
      <div className="quota-chip__body min-w-0">
        {mobile ? <div className="quota-chip__title">{title}</div> : null}
        <div className="quota-chip__numbers">
          <span className={[
            'tabular-nums font-semibold text-neutral-900 dark:text-neutral-100',
            compact ? 'text-[0.92rem]' : 'text-sm',
          ].join(' ')}>
            {valueText}
          </span>
          {!mobile && !compact && etaText ? (
            <span className={[
              'tabular-nums text-neutral-500 dark:text-neutral-400 shrink-0',
              compact ? 'text-[11px]' : 'text-xs',
            ].join(' ')}>
              {etaText}
            </span>
          ) : null}
        </div>
        {mobile ? (
          <div className="quota-chip__timer tabular-nums">{stateText}</div>
        ) : null}
      </div>
    </div>
  );
}

function QuotaStatusBar({ className = '', compact = false, mobile = false }) {
  const tasks = useQuotaBucket('tasks');
  const top = useQuotaBucket('top');

  if (!tasks && !top) return null;

  return (
    <div className={[
      mobile
        ? 'quota-status-mobile flex min-w-0 items-stretch gap-2'
        : compact
          ? 'quota-status-compact flex items-center gap-2 shrink-0'
          : 'hidden md:flex flex-wrap items-center justify-center gap-2',
      className,
    ].join(' ')}>
      <BucketChip icon={Zap} title="Задачи" bucket={tasks} compact={compact || mobile} mobile={mobile} />
      <BucketChip icon={Trophy} title="Рейтинг" bucket={top} compact={compact || mobile} mobile={mobile} />
    </div>
  );
}

export default React.memo(QuotaStatusBar);
