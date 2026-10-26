import React from 'react';
import { createPortal } from 'react-dom';
import { Copy, FileJson, GitCompare, Sparkles, Upload, X } from 'lucide-react';

import { Badge, Button, Card, Textarea } from '../../../components/ui';
import JsonImportHelp from './JsonImportHelp';

export default function JsonTaskGraphImportDialog({
  open,
  progress = null,
  busy = false,
  preview = 'пусто',
  text = '',
  source = 'Текстовое поле',
  docsOpen = false,
  onClose,
  onFile,
  onCopy,
  onFormat,
  onToggleDocs,
  onTextChange,
  onPaste,
  onClear,
  onCopyAiPrompt,
  onCopyExample,
  onUseExample,
  onPrepareDiff,
}) {
  React.useEffect(() => {
    if (!open || typeof document === 'undefined') return undefined;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, [open]);

  const inputBytes = React.useMemo(() => new Blob([text]).size, [text]);
  const largeInput = text.length > 350_000;
  const hasText = Boolean(text.trim());

  if (!open || typeof document === 'undefined') return null;

  return createPortal((
    <div
      className="tf-modal-backdrop fixed inset-0 z-50 flex items-center justify-center overflow-hidden bg-black/55 p-2 sm:p-4"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onClose?.();
      }}
      onDragOver={(event) => {
        if (!busy && Array.from(event.dataTransfer?.types || []).includes('Files')) event.preventDefault();
      }}
      onDrop={(event) => {
        if (busy || !event.dataTransfer?.files?.length) return;
        event.preventDefault();
        onFile?.(event.dataTransfer.files[0]);
      }}
    >
      <Card className="tf-modal-panel flex max-h-[calc(100dvh-1rem)] w-full max-w-5xl min-w-0 flex-col overflow-hidden rounded-[28px] border border-[rgba(var(--border)/0.8)] bg-[rgb(var(--card))] p-0 shadow-2xl sm:max-h-[calc(100dvh-2rem)]">
        <div className="flex shrink-0 flex-col gap-3 border-b border-[rgba(var(--border)/0.65)] px-4 py-4 sm:flex-row sm:items-center sm:justify-between sm:px-6">
          <div>
            <div className="flex items-center gap-2 text-xl font-semibold"><FileJson size={20} /> Импорт JSON</div>
            <div className="mt-1 text-sm text-neutral-500">Загрузите файл или вставьте JSON. Перед применением TaskForge покажет точный список изменений.</div>
          </div>
          <Button variant="outline" onClick={onClose} title="Закрыть"><X size={16} /> Закрыть</Button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 py-4 sm:px-6">
          {progress}
          <div className="flex flex-col gap-3 rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--muted))]/20 p-3 lg:flex-row lg:items-center lg:justify-between">
            <div className="flex flex-wrap items-center gap-2">
              <Sparkles size={18} />
              <span className="font-semibold">Файл для импорта</span>
              <Badge variant="outline">{preview}</Badge>
            </div>
            <div className="flex flex-wrap gap-2">
              <label className={`btn-outline cursor-pointer${busy ? ' pointer-events-none opacity-60' : ''}`}>
                <Upload size={16} /> Выбрать файл
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
              <Button variant="outline" onClick={onCopy} disabled={busy || !hasText}><Copy size={16} /> Копировать</Button>
              <Button variant="outline" onClick={onFormat} disabled={busy || !hasText || largeInput} title={largeInput ? 'Большой JSON форматируйте в исходном файле' : undefined}>Форматировать</Button>
              {hasText ? <Button variant="outline" onClick={onClear} disabled={busy}>Очистить</Button> : null}
              <Button variant="outline" onClick={onToggleDocs} disabled={busy}><FileJson size={16} /> {docsOpen ? 'Скрыть справку' : 'Справка'}</Button>
            </div>
          </div>

          <div className="mt-3 rounded-xl border border-[rgba(var(--border)/0.55)] px-3 py-2 text-xs" role="status" aria-live="polite">
            <div className="break-all">Источник: <strong>{source}</strong></div>
            <div className="mt-1">Содержимое: <strong>{inputBytes.toLocaleString('ru-RU')} байт</strong> ({(inputBytes / 1048576).toFixed(2)} МиБ), {text.length.toLocaleString('ru-RU')} символов</div>
            <div className="mt-1 text-neutral-500">JSON-файлы до 24 МиБ загружайте через «Выбрать файл» или перетаскиванием .json в это окно. Проверяйте имя файла и размер перед импортом.</div>
          </div>
          {largeInput ? (
            <div className="mt-4 rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--muted))]/15 p-4 text-sm">
              <div className="font-semibold">Большой JSON загружен целиком</div>
              <p className="mt-2 text-neutral-500">Чтобы браузер не зависал, текст не выводится целиком в редактор. Проверка и импорт используют полное содержимое ({inputBytes.toLocaleString('ru-RU')} байт), а не этот фрагмент. Чтобы вставить другой JSON, нажмите «Очистить» или выберите новый файл.</p>
              <pre className="mt-3 max-h-36 overflow-auto whitespace-pre-wrap break-all rounded-xl bg-[rgb(var(--card))] p-3 text-xs">{text.slice(0, 600)}…</pre>
            </div>
          ) : (
            <Textarea
              rows={18}
              value={text}
              onChange={(event) => onTextChange?.(event.target.value)}
              onPaste={onPaste}
              spellCheck={false}
              placeholder="Вставьте JSON-граф для импорта"
              className="mt-4 h-[clamp(240px,42dvh,560px)] min-h-[240px] resize-y font-mono text-xs leading-5"
            />
          )}

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
            На следующем шаге вы увидите, что будет создано, обновлено, удалено или оставлено без изменений. До подтверждения курс не меняется.
          </div>
          <Button className="shrink-0" onClick={onPrepareDiff} disabled={busy || !hasText}>
            <GitCompare size={16} /> {busy ? 'Проверяю…' : 'Проверить импорт'}
          </Button>
        </div>
      </Card>
    </div>
  ), document.body);
}
