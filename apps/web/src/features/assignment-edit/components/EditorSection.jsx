import React from 'react';
import { ChevronDown, ChevronRight, Settings2 } from 'lucide-react';
import { Card } from '../../../components/ui';

export const EditorSection = React.memo(function EditorSection({ id, icon: Icon, title, description, summary, open, onToggle, actions, children }) {
  return (
    <Card id={id} className="overflow-hidden p-0 scroll-mt-24">
      <button type="button" className="flex w-full items-start justify-between gap-4 px-5 py-4 text-left hover:bg-[rgba(var(--muted)/0.45)]" onClick={onToggle}>
        <div className="flex min-w-0 items-start gap-3">
          <div className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-2xl border border-[rgba(var(--border)/0.75)] bg-[rgb(var(--card))]">
            {Icon ? <Icon size={18} /> : <Settings2 size={18} />}
          </div>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="text-lg font-semibold leading-tight">{title}</h2>
              {summary ? <span className="text-xs text-neutral-500">{summary}</span> : null}
            </div>
            {description ? <div className="mt-1 text-sm text-neutral-500">{description}</div> : null}
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {actions ? <div onClick={(event) => event.stopPropagation()}>{actions}</div> : null}
          {open ? <ChevronDown size={18} /> : <ChevronRight size={18} />}
        </div>
      </button>
      {open ? <div className="border-t border-[rgba(var(--border)/0.65)] p-5">{children}</div> : null}
    </Card>
  );
});

export const SmallCheck = React.memo(function SmallCheck({ label, checked, onChange, disabled = false }) {
  return (
    <label className={`inline-flex items-center gap-2 rounded-xl border border-[rgba(var(--border)/0.65)] px-3 py-2 text-sm ${disabled ? 'opacity-60' : 'cursor-pointer'}`}>
      <input type="checkbox" checked={Boolean(checked)} disabled={disabled} onChange={(event) => onChange(event.target.checked)} />
      {label}
    </label>
  );
});
