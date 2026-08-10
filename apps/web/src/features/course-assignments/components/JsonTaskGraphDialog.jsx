import React from 'react';
import { Copy, Download, FileJson, GitCompare, Sparkles, Upload, X } from 'lucide-react';

import { Badge, Button, Card, Textarea } from '../../../components/ui';
import JsonImportHelp from './JsonImportHelp';

export default function JsonTaskGraphDialog({
  open,
  busy = false,
  exportBusy = false,
  preview = 'пусто',
  text = '',
  docsOpen = false,
  onClose,
  onExport,
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
  if (!open) return null;

  return (
    <div
      className="tf-modal-backdrop fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/55 px-3 py-6 sm:px-6"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget && !busy) onClose?.();
      }}
    >
      <Card className="tf-modal-panel w-full max-w-5xl rounded-[28px] border border-[rgba(var(--border)/0.8)] bg-[rgb(var(--card))] p-4 shadow-2xl sm:p-6">
        <div className="flex flex-col gap-3 border-b border-[rgba(var(--border)/0.65)] pb-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex items-center gap-2 text-xl font-semibold"><Sparkles size={20} /> JSON-граф заданий</div>
          <Button variant="outline" onClick={onClose} disabled={busy} title="Закрыть"><X size={16} /> Закрыть</Button>
        </div>

        <div className="mt-5 flex flex-col gap-3 rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--muted))]/20 p-3 lg:flex-row lg:items-center lg:justify-between">
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

        <Textarea
          rows={28}
          value={text}
          onChange={(event) => onTextChange?.(event.target.value)}
          spellCheck={false}
          placeholder="Вставьте JSON-граф"
          className="mt-4 min-h-[560px] font-mono text-xs leading-5"
        />

        {docsOpen ? (
          <JsonImportHelp
            busy={busy}
            onCopyAiPrompt={onCopyAiPrompt}
            onCopyExample={onCopyExample}
            onUseExample={onUseExample}
          />
        ) : null}

        <div className="mt-4 flex flex-col gap-3 xl:flex-row xl:items-center xl:justify-between">
          <div className="text-xs text-neutral-500">
            JSON задаёт задания, порядок, ветви и эффекты стрелок. Расположение рассчитывает TaskForge.
          </div>
          <Button onClick={onPrepareDiff} disabled={busy}>
            <GitCompare size={16} /> {busy ? 'Проверяю…' : 'Проверить и импортировать'}
          </Button>
        </div>
      </Card>
    </div>
  );
}
