import React, { forwardRef, useCallback, useImperativeHandle, useRef, useState } from 'react';
import { Loader2, Paperclip, Send, Square } from 'lucide-react';
import { Button, Textarea } from '../../../components/ui';

const TEMPLATES = [
  ['сложные места', 'Найди сложные места в курсе и предложи задания для плавного перехода.'],
  ['пошаговые задания', 'Сделай пошаговые задания: дружелюбно, с одной новой идеей на шаг.'],
  ['в стиле курса', 'Создай задачи в стиле курса без резкого скачка сложности.'],
  ['рейтинги', 'Пересчитай рейтинги всех заданий курса по сложности и покажи патчи с диффами.'],
];

const AgentComposer = forwardRef(function AgentComposer({
  sending = false,
  uploading = false,
  activeRun = false,
  onSend,
  onStop,
}, ref) {
  const [text, setText] = useState('');
  const [files, setFiles] = useState([]);
  const inputRef = useRef(null);
  const textareaRef = useRef(null);

  const setDraft = useCallback((value) => {
    setText(String(value || ''));
    requestAnimationFrame(() => textareaRef.current?.focus());
  }, []);

  useImperativeHandle(ref, () => ({
    setText: setDraft,
    focus: () => textareaRef.current?.focus(),
    getFiles: () => [...files],
    clear: () => {
      setText('');
      setFiles([]);
    },
  }), [files, setDraft]);

  const submit = useCallback(async (event) => {
    event?.preventDefault?.();
    const normalized = text.trim();
    if ((normalized.length === 0 && files.length === 0) || sending || uploading) return;
    const accepted = await onSend?.({ text: normalized, files: [...files] });
    if (accepted !== false) {
      setText('');
      setFiles([]);
    }
  }, [files, onSend, sending, text, uploading]);

  const addFiles = useCallback((event) => {
    const selected = Array.from(event.target.files || []);
    if (selected.length) setFiles((current) => [...current, ...selected].slice(0, 10));
    event.target.value = '';
  }, []);

  return (
    <div className="border-t border-neutral-200/70 dark:border-neutral-800/70 bg-[rgb(var(--card))]/95 p-3">
      {files.length > 0 ? (
        <div className="mx-auto mb-2 flex max-w-5xl flex-wrap gap-2 rounded-2xl border border-neutral-200/70 bg-white/70 px-3 py-2 text-xs dark:border-neutral-800/70 dark:bg-neutral-950/30">
          {files.map((file, index) => (
            <span key={`${file.name}-${file.size}-${index}`} className="inline-flex max-w-[18rem] items-center gap-1.5 rounded-xl bg-neutral-100 px-2 py-1 dark:bg-neutral-900">
              <Paperclip size={13} />
              <span className="truncate">{file.name}</span>
              <button type="button" className="text-neutral-400 hover:text-danger-600" onClick={() => setFiles((current) => current.filter((_, itemIndex) => itemIndex !== index))} aria-label="Убрать файл">×</button>
            </span>
          ))}
        </div>
      ) : null}

      <form onSubmit={submit} className="mx-auto flex max-w-5xl items-end gap-2">
        <div className="min-w-0 flex-1 rounded-2xl border border-neutral-200/80 dark:border-neutral-800/80 bg-white/80 dark:bg-neutral-950/40 px-3 py-2 shadow-soft focus-within:border-brand-400">
          <Textarea
            ref={textareaRef}
            value={text}
            onChange={(event) => setText(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();
                submit(event);
              }
            }}
            rows={1}
            placeholder="Напиши запрос: проанализируй курс, найди сложные места, поменяй рейтинги через патчи..."
            className="!min-h-[2.5rem] !max-h-28 !border-0 !bg-transparent !p-0 !shadow-none resize-none text-sm"
          />
          <div className="mt-1 flex flex-wrap items-center gap-2 text-[11px] text-neutral-500 dark:text-neutral-400">
            <span>Enter — отправить, Shift+Enter — новая строка</span>
            {TEMPLATES.map(([label, value], index) => (
              <React.Fragment key={label}>
                {index > 0 ? <span>·</span> : null}
                <button type="button" className="hover:text-brand-600" onClick={() => setDraft(value)}>{label}</button>
              </React.Fragment>
            ))}
          </div>
        </div>
        <input ref={inputRef} type="file" multiple className="hidden" onChange={addFiles} />
        <Button type="button" variant="outline" onClick={() => inputRef.current?.click()} className="!min-w-0 !px-3 h-12" title="Прикрепить файлы к AI-контексту">
          <Paperclip size={15} />
        </Button>
        {activeRun ? (
          <Button type="button" variant="outline" onClick={onStop} className="!min-w-0 !px-3 h-12">
            <Square size={15} />
          </Button>
        ) : null}
        <Button type="submit" disabled={sending || uploading || (!text.trim() && files.length === 0)} className="!min-w-0 h-12 px-4">
          {sending || uploading ? <Loader2 size={17} className="animate-spin" /> : <Send size={17} />}
          <span className="hidden sm:inline">Отправить</span>
        </Button>
      </form>
    </div>
  );
});

export default React.memo(AgentComposer);
