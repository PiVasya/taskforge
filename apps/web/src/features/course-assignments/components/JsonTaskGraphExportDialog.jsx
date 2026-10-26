import React from 'react';
import { TRANSFER_EXPORT_PRESETS } from '../graphTransferModel';
import { createPortal } from 'react-dom';
import { Download, Settings2, X } from 'lucide-react';

import { Button, Card } from '../../../components/ui';
import { taskGraphExportIssue } from '../courseTaskGraphJson';

function ExportOption({ checked, onChange, label, hint, disabled }) {
  return (
    <label className="flex cursor-pointer items-start gap-2 rounded-xl border border-[rgba(var(--border)/0.6)] px-3 py-2 text-sm">
      <input type="checkbox" disabled={disabled} checked={Boolean(checked)} onChange={(event) => onChange?.(event.target.checked)} className="mt-0.5" />
      <span className="min-w-0">
        <span className="font-medium">{label}</span>
        {hint ? <span className="ml-1 text-xs text-neutral-500">{hint}</span> : null}
      </span>
    </label>
  );
}

export default function JsonTaskGraphExportDialog({
  open,
  progress = null,
  busy = false,
  onClose,
  onExport,
  exportOptions = {},
  onExportOptionChange,
  onPreset,
}) {
  React.useEffect(() => {
    if (!open || typeof document === 'undefined') return undefined;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, [open]);

  if (!open || typeof document === 'undefined') return null;
  const exportIssue = taskGraphExportIssue(exportOptions);

  return createPortal((
    <div
      className="tf-modal-backdrop fixed inset-0 z-50 flex items-center justify-center overflow-hidden bg-black/55 p-2 sm:p-4"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onClose?.();
      }}
    >
      <Card className="tf-modal-panel flex max-h-[calc(100dvh-2rem)] w-full max-w-3xl min-w-0 flex-col overflow-hidden rounded-[28px] border border-[rgba(var(--border)/0.8)] bg-[rgb(var(--card))] p-0 shadow-2xl">
        <div className="flex shrink-0 flex-col gap-3 border-b border-[rgba(var(--border)/0.65)] px-4 py-4 sm:flex-row sm:items-center sm:justify-between sm:px-6">
          <div>
            <div className="flex items-center gap-2 text-xl font-semibold"><Download size={20} /> Экспорт JSON</div>
            <div className="mt-1 text-sm text-neutral-500">Скачайте текущий граф курса в отдельный JSON-файл.</div>
          </div>
          <Button variant="outline" onClick={onClose} title="Закрыть"><X size={16} /> Закрыть</Button>
        </div>

        <div className="min-h-0 overflow-y-auto px-4 py-4 sm:px-6">
          {progress}
          <div className="mb-3 flex flex-wrap gap-2">
            {Object.entries({ layout: 'Координаты', structure: 'Структура карты',
              content: 'Условия без тестов', full: 'Полный экспорт' }).map(([key, title]) =>
              <Button key={key} variant="outline" disabled={busy} onClick={() => onPreset?.(TRANSFER_EXPORT_PRESETS[key])}>{title}</Button>)}
          </div>
          <div className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--muted))]/15">
            <div className="flex items-center gap-2 border-b border-[rgba(var(--border)/0.55)] px-4 py-3 text-sm font-semibold">
              <Settings2 size={16} /> Что включить в экспорт
            </div>
            <div className="grid gap-2 p-3 sm:grid-cols-2">
              <ExportOption disabled={busy} checked={exportOptions.includeIds} onChange={(value) => onExportOptionChange?.('includeIds', value)} label="ID заданий" hint="нужны для обновления существующих" />
              <ExportOption disabled={busy} checked={exportOptions.includeContent} onChange={(value) => onExportOptionChange?.('includeContent', value)} label="Условия и настройки" />
              <ExportOption disabled={busy} checked={exportOptions.includeChecks} onChange={(value) => onExportOptionChange?.('includeChecks', value)} label="Тесты и ответы" />
              <ExportOption disabled={busy} checked={exportOptions.includeVisibility} onChange={(value) => onExportOptionChange?.('includeVisibility', value)} label="Видимость заданий" />
              <ExportOption disabled={busy} checked={exportOptions.includeConnections} onChange={(value) => onExportOptionChange?.('includeConnections', value)} label="Связи" hint="кто за кем идёт" />
              <ExportOption disabled={busy} checked={exportOptions.includeConnectionAccess} onChange={(value) => onExportOptionChange?.('includeConnectionAccess', value)} label="Эффекты стрелок" />
              <ExportOption disabled={busy} checked={exportOptions.includeLayout} onChange={(value) => onExportOptionChange?.('includeLayout', value)} label="Позиции и масштаб" />
              <ExportOption disabled={busy} checked={exportOptions.includeGuide} onChange={(value) => onExportOptionChange?.('includeGuide', value)} label="Обучалка внутри JSON" hint="большая справка для ИИ или человека" />
            </div>
            {exportIssue ? <div role="alert" className="border-t border-red-400/60 p-3 text-sm text-red-700 dark:text-red-200">{exportIssue}</div> : null}
            {!exportOptions.includeIds ? (
              <div className="border-t border-[rgba(var(--border)/0.55)] px-4 py-3 text-xs text-amber-700 dark:text-amber-200">
                Без ID задания при обратном импорте будут считаться новыми. Для переноса или редактирования существующего графа оставьте ID включёнными.
              </div>
            ) : null}
          </div>
        </div>

        <div className="flex shrink-0 flex-col gap-3 border-t border-[rgba(var(--border)/0.65)] bg-[rgb(var(--card))] px-4 py-3 sm:flex-row sm:items-center sm:justify-between sm:px-6">
          <div className="text-xs text-neutral-500">Экспорт ничего не изменяет в курсе.</div>
          <Button onClick={onExport} disabled={busy || Boolean(exportIssue)}>
            <Download size={16} /> {busy ? 'Готовлю файл…' : 'Скачать JSON'}
          </Button>
        </div>
      </Card>
    </div>
  ), document.body);
}
