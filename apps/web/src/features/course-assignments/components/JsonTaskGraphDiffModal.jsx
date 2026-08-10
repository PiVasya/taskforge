import React from 'react';
import { AlertTriangle, ArrowRight, FileJson, GitCompare, X } from 'lucide-react';

import { Badge, Button, Card } from '../../../components/ui';
import { shortTaskGraphValue } from '../courseTaskGraphJson';


function EffectBadges({ row }) {
  const hidden = row.hidden === 'start' ? 'Скрытие · старт' : row.hidden === 'stop' ? 'Скрытие · конец' : '';
  const sequential = row.sequential === 'start' ? 'По одному · старт' : row.sequential === 'stop' ? 'По одному · конец' : '';
  if (!hidden && !sequential) return <Badge variant="outline">Обычная</Badge>;
  return (
    <>
      {hidden ? <Badge variant="outline">{hidden}</Badge> : null}
      {sequential ? <Badge variant="outline">{sequential}</Badge> : null}
    </>
  );
}

function ConnectionList({ title, rows, removed = false }) {
  if (!rows?.length) return null;
  return (
    <details open={!removed} className={`rounded-2xl border ${removed ? 'border-amber-400/60 bg-amber-500/5' : 'border-[rgba(var(--border)/0.7)] bg-[rgb(var(--muted))]/15'}`}>
      <summary className="cursor-pointer select-none px-4 py-3 font-semibold">
        {title} · {rows.length}
      </summary>
      <div className="max-h-[420px] space-y-2 overflow-auto border-t border-[rgba(var(--border)/0.55)] p-3">
        {rows.map((row) => (
          <div key={`${row.status}:${row.index}:${row.from}:${row.to}`} className="rounded-xl border border-[rgba(var(--border)/0.62)] bg-[rgb(var(--card))]/70 p-3">
            <div className="flex flex-col gap-2 lg:flex-row lg:items-center lg:justify-between">
              <div className="flex min-w-0 items-center gap-2 text-sm">
                <span className="min-w-0 break-words font-medium">{row.fromLabel}</span>
                <ArrowRight size={15} className="shrink-0 text-neutral-500" />
                <span className="min-w-0 break-words font-medium">{row.toLabel}</span>
              </div>
              <div className="flex flex-wrap gap-2">
                <Badge variant={removed ? 'warning' : row.status === 'add' ? 'success' : 'secondary'}>
                  {removed ? 'Удалить' : row.status === 'add' ? 'Добавить' : 'Без изменений'}
                </Badge>
                <EffectBadges row={row} />
              </div>
            </div>
            <div className="mt-2 break-all text-xs text-neutral-500">{row.from} → {row.to}</div>
          </div>
        ))}
      </div>
    </details>
  );
}

function Stat({ label, value, danger = false }) {
  return (
    <div className={`rounded-2xl border p-3 ${danger ? 'border-red-400/70 bg-red-500/10' : 'border-[rgba(var(--border)/0.65)]'}`}>
      <div className="text-xs uppercase tracking-wide text-neutral-500">{label}</div>
      <div className="mt-1 text-2xl font-semibold">{value}</div>
    </div>
  );
}

export default function JsonTaskGraphDiffModal({ open, diff, busy = false, onClose, onApply }) {
  if (!open || !diff) return null;

  return (
    <div
      className="tf-modal-backdrop fixed inset-0 z-[60] flex items-start justify-center overflow-y-auto bg-black/60 px-3 py-6 sm:px-6"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget && !busy) onClose?.();
      }}
    >
      <Card className="tf-modal-panel w-full max-w-6xl rounded-[28px] border border-[rgba(var(--border)/0.8)] bg-[rgb(var(--card))] p-4 shadow-2xl sm:p-6">
        <div className="flex flex-col gap-3 border-b border-[rgba(var(--border)/0.65)] pb-4 lg:flex-row lg:items-center lg:justify-between">
          <div>
            <div className="flex items-center gap-2 text-xl font-semibold"><GitCompare size={20} /> Проверка импорта</div>
            <div className="mt-1 text-sm text-neutral-500">Задания и связи будут применены одним импортом.</div>
          </div>
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" onClick={onClose} disabled={busy}><X size={16} /> Назад</Button>
            <Button onClick={onApply} disabled={busy || diff.total === 0 || diff.validationErrorCount > 0}>
              <FileJson size={16} /> {busy ? 'Импортирую…' : 'Импортировать'}
            </Button>
          </div>
        </div>

        <div className="mt-4 grid grid-cols-2 gap-2 sm:grid-cols-3 xl:grid-cols-6">
          <Stat label="Заданий" value={diff.total} />
          <Stat label="Создать" value={diff.createCount} />
          <Stat label="Обновить" value={diff.updateCount} />
          <Stat label="Без изменений" value={diff.unchangedCount} />
          <Stat label="Связей" value={diff.connectionCount} />
          <Stat label="Ошибок" value={diff.validationErrorCount} danger={diff.validationErrorCount > 0} />
        </div>

        <div className="mt-3 flex flex-wrap gap-2">
          <Badge variant="outline">Добавится связей: {diff.connectionAddedCount}</Badge>
          <Badge variant="outline">Удалится связей: {diff.connectionRemovedCount}</Badge>
          <Badge variant="outline">Сохранится связей: {diff.connectionUnchangedCount}</Badge>
          {diff.unplacedCount > 0 ? <Badge intent="warning">Вне карты: {diff.unplacedCount}</Badge> : null}
          {diff.legacy ? <Badge intent="warning">Без графа</Badge> : null}
        </div>

        {diff.legacy ? (
          <div className="mt-4 rounded-2xl border border-amber-400/70 bg-amber-500/10 px-4 py-3 text-sm text-amber-950 dark:text-amber-100">
            Этот файл содержит только задания. Они импортируются без изменения связей карты.
          </div>
        ) : null}

        {diff.graphIssues?.length ? (
          <div className="mt-4 rounded-2xl border border-red-400/70 bg-red-500/10 p-4 text-sm text-red-950 dark:text-red-100">
            <div className="flex items-center gap-2 font-semibold"><AlertTriangle size={17} /> Исправьте JSON</div>
            <div className="mt-2 space-y-1">
              {diff.graphIssues.map((issue, index) => (
                <div key={`${issue.path}:${index}`}><code>{issue.path}</code> — {issue.message}</div>
              ))}
            </div>
          </div>
        ) : null}

        {diff.duplicateTitleCount > 0 ? (
          <div className="mt-4 rounded-2xl border border-amber-400/70 bg-amber-500/10 px-4 py-3 text-sm text-amber-950 dark:text-amber-100">
            Возможные дубли по названию: {diff.duplicateTitleCount}
          </div>
        ) : null}

        {!diff.legacy ? (
          <div className="mt-4 grid gap-3 xl:grid-cols-2">
            <ConnectionList title="Маршруты из JSON" rows={diff.connectionRows} />
            <ConnectionList title="Связи, которые исчезнут" rows={diff.removedConnectionRows} removed />
          </div>
        ) : null}

        <div className="mt-4 space-y-3 pb-2">
          {diff.rows.map((row) => (
            <div key={`${row.index}:${row.id || row.key}`} className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--muted))]/20 p-3">
              <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    <Badge variant={row.action === 'create' ? 'success' : row.action === 'update' ? 'outline' : 'secondary'}>
                      {row.action === 'create' ? 'Создать' : row.action === 'update' ? 'Обновить' : 'Без изменений'}
                    </Badge>
                    <Badge variant="outline">{row.type}</Badge>
                    {row.duplicateTitle ? <Badge intent="danger">возможный дубль</Badge> : null}
                  </div>
                  <div className="mt-2 break-words font-semibold">{row.title}</div>
                  <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1 break-all text-xs text-neutral-500">
                    {row.key ? <span>key: {row.key}</span> : null}
                    <span>{row.id ? `id: ${row.id}` : 'Новое задание'}</span>
                  </div>
                </div>
                <div className="text-xs text-neutral-500">#{row.index + 1}</div>
              </div>

              {row.issues.length ? (
                <div className="mt-3 rounded-xl border border-red-400/60 bg-red-500/10 px-3 py-2 text-sm text-red-950 dark:text-red-100">
                  {row.issues.map((issue) => <div key={issue}>{issue}</div>)}
                </div>
              ) : row.changes.length ? (
                <div className="mt-3 space-y-2">
                  {row.changes.map((change) => (
                    <div key={change.key} className="rounded-xl border border-[rgba(var(--border)/0.65)] p-3">
                      <div className="mb-2 text-sm font-semibold">{change.label}</div>
                      <div className="grid gap-2 lg:grid-cols-2">
                        <div>
                          <div className="mb-1 text-xs uppercase tracking-wide text-neutral-500">Было</div>
                          <pre className="max-h-44 overflow-auto whitespace-pre-wrap rounded-lg bg-[rgb(var(--card))] p-2 text-xs leading-5">{shortTaskGraphValue(change.before)}</pre>
                        </div>
                        <div>
                          <div className="mb-1 text-xs uppercase tracking-wide text-neutral-500">Станет</div>
                          <pre className="max-h-44 overflow-auto whitespace-pre-wrap rounded-lg bg-[rgb(var(--card))] p-2 text-xs leading-5">{shortTaskGraphValue(change.after)}</pre>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              ) : null}
            </div>
          ))}
        </div>
      </Card>
    </div>
  );
}
