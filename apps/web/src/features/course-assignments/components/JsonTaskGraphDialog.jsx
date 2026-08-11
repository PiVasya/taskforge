import React from 'react';
import { createPortal } from 'react-dom';
import { Copy, Download, FileJson, GitCompare, Settings2, Sparkles, Upload, X } from 'lucide-react';

import { Badge, Button, Card, Textarea } from '../../../components/ui';
import JsonImportHelp from './JsonImportHelp';

function ExportOption({ checked, onChange, label, hint }) {
  return (
    <label className="flex cursor-pointer items-start gap-2 rounded-xl border border-[rgba(var(--border)/0.6)] px-3 py-2 text-sm">
      <input type="checkbox" checked={Boolean(checked)} onChange={(event) => onChange?.(event.target.checked)} className="mt-0.5" />
      <span className="min-w-0">
        <span className="font-medium">{label}</span>
        {hint ? <span className="ml-1 text-xs text-neutral-500">{hint}</span> : null}
      </span>
    </label>
  );
}

export default function JsonTaskGraphDialog({
  open,
  busy = false,
  exportBusy = false,
  preview = 'пусто',
  text = '',
  docsOpen = false,
  onClose,
  onExport,
  exportOptions = {},
  onExportOptionChange,
  onFile,
  onCopy,
  onFormat,
  onToggleDocs,
  onTextChange,
  onCopyAiPrompt,
  onCopyExample,
  onUseExample,
  onPrepareDiff,
}) {
  React.useEffect(() => {
    if (!(open) || typeof document === 'undefined') return undefined;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, [open]);

  if (!open || typeof document === 'undefined') return null;

  return createPortal((
    <div
      className="tf-modal-backdrop fixed inset-0 z-50 flex items-center justify-center overflow-hidden bg-black/55 p-2 sm:p-4"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget && !busy) onClose?.();
      }}
    >
      <Card className="tf-modal-panel flex max-h-[calc(100dvh-1rem)] w-full max-w-5xl min-w-0 flex-col overflow-hidden rounded-[28px] border border-[rgba(var(--border)/0.8)] bg-[rgb(var(--card))] p-0 shadow-2xl sm:max-h-[calc(100dvh-2rem)]">
        <div className="flex shrink-0 flex-col gap-3 border-b border-[rgba(var(--border)/0.65)] px-4 py-4 sm:flex-row sm:items-center sm:justify-between sm:px-6">
          <div className="flex items-center gap-2 text-xl font-semibold"><Sparkles size={20} /> JSON-граф заданий</div>
          <Button variant="outline" onClick={onClose} disabled={busy} title="Закрыть"><X size={16} /> Закрыть</Button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 py-4 sm:px-6">
          <div className="flex flex-col gap-3 rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--muted))]/20 p-3 lg:flex-row lg:items-center lg:justify-between">
          <div className="flex flex-wrap items-center gap-2">
            <FileJson size={18} />
            <span className="font-semibold">Граф</span>
            <Badge variant="outline">{preview}</Badge>
          </div>
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" onClick={onExport} disabled={exportBusy || busy}>
              <Download size={16} /> {exportBusy ? 'Экспорт…' : 'Экспорт'}
            </Button>
            <label className={`btn-outline cursor-pointer${busy ? ' pointer-events-none opacity-60' : ''}`}>
              <Upload size={16} /> Файл
              <input
                type="file"
                accept="application/json,.json"
                className="hidden"
                disabled={busy}
                onChange={(event) => {
                  const file = event.target.files?.[0];
                  if (file) onFile?.(file);
                  event.target.value = '';
                }}
              />
            </label>
            <Button variant="outline" onClick={onCopy} disabled={busy || !text.trim()}><Copy size={16} /> Копировать</Button>
            <Button variant="outline" onClick={onFormat} disabled={busy || !text.trim()}>Форматировать</Button>
            <Button variant="outline" onClick={onToggleDocs} disabled={busy}><FileJson size={16} /> {docsOpen ? 'Скрыть справку' : 'Справка'}</Button>
          </div>
          </div>

          <details className="mt-3 rounded-2xl border border-[rgba(var(--border)/0.65)] bg-[rgb(var(--muted))]/10">
          <summary className="flex cursor-pointer select-none items-center gap-2 px-4 py-3 text-sm font-semibold">
            <Settings2 size={16} /> Что экспортировать
          </summary>
          <div className="grid gap-2 border-t border-[rgba(var(--border)/0.55)] p-3 sm:grid-cols-2 xl:grid-cols-4">
            <ExportOption checked={exportOptions.includeIds} onChange={(value) => onExportOptionChange?.('includeIds', value)} label="ID заданий" hint="для обновления существующих" />
            <ExportOption checked={exportOptions.includeContent} onChange={(value) => onExportOptionChange?.('includeContent', value)} label="Условия и настройки" />
            <ExportOption checked={exportOptions.includeChecks} onChange={(value) => onExportOptionChange?.('includeChecks', value)} label="Тесты и ответы" />
            <ExportOption checked={exportOptions.includeVisibility} onChange={(value) => onExportOptionChange?.('includeVisibility', value)} label="Видимость заданий" />
            <ExportOption checked={exportOptions.includeConnections} onChange={(value) => onExportOptionChange?.('includeConnections', value)} label="Связи" hint="кто за кем идёт" />
            <ExportOption checked={exportOptions.includeConnectionAccess} onChange={(value) => onExportOptionChange?.('includeConnectionAccess', value)} label="Эффекты стрелок" />
            <ExportOption checked={exportOptions.includeLayout} onChange={(value) => onExportOptionChange?.('includeLayout', value)} label="Позиции и масштаб" />
          </div>
          {!exportOptions.includeIds ? (
            <div className="border-t border-[rgba(var(--border)/0.55)] px-4 py-3 text-xs text-amber-700 dark:text-amber-200">
              Без ID задания при обратном импорте считаются новыми. Для перестановки существующей карты оставьте ID включёнными.
            </div>
          ) : null}
          </details>

          <Textarea
          rows={18}
          value={text}
          onChange={(event) => onTextChange?.(event.target.value)}
          spellCheck={false}
          placeholder="Вставьте JSON-граф"
          className="mt-4 h-[clamp(240px,42dvh,560px)] min-h-[240px] resize-y font-mono text-xs leading-5"
          />

          {docsOpen ? (
            <JsonImportHelp
            busy={busy}
            onCopyAiPrompt={onCopyAiPrompt}
            onCopyExample={onCopyExample}
            onUseExample={onUseExample}
            />
          ) : null}
        </div>

        <div className="flex shrink-0 flex-col gap-3 border-t border-[rgba(var(--border)/0.65)] bg-[rgb(var(--card))] px-4 py-3 sm:px-6 xl:flex-row xl:items-center xl:justify-between">
          <div className="min-w-0 text-xs text-neutral-500">
            ID связывает JSON с существующим заданием. Позиции хранятся отдельно в layout и могут импортироваться независимо от условий.
          </div>
          <Button className="shrink-0" onClick={onPrepareDiff} disabled={busy}>
            <GitCompare size={16} /> {busy ? 'Проверяю…' : 'Проверить и импортировать'}
          </Button>
        </div>
      </Card>
    </div>
  ), document.body);
}
