import React from 'react';
import { createPortal } from 'react-dom';
import { AlertTriangle, Trash2, X } from 'lucide-react';

import { Button } from '../ui';
import { canConfirmCourseDeletion, normalizedCourseDeleteTitle } from './courseDeleteConfirmation';

export default function CourseDeleteConfirmationDialog({ open, courseTitle, busy = false, onCancel, onConfirm }) {
  const [typedTitle, setTypedTitle] = React.useState('');
  const [acknowledged, setAcknowledged] = React.useState(false);
  const inputRef = React.useRef(null);
  const expectedTitle = normalizedCourseDeleteTitle(courseTitle) || 'Без названия';
  const canDelete = canConfirmCourseDeletion({ expectedTitle, typedTitle, acknowledged, busy });

  React.useEffect(() => {
    if (!open) return undefined;
    setTypedTitle('');
    setAcknowledged(false);
    const timer = window.setTimeout(() => inputRef.current?.focus(), 0);
    return () => window.clearTimeout(timer);
  }, [open, expectedTitle]);

  React.useEffect(() => {
    if (!open) return undefined;
    const onKeyDown = (event) => {
      if (event.key !== 'Escape' || busy) return;
      event.preventDefault();
      event.stopPropagation();
      onCancel?.();
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [busy, onCancel, open]);

  if (!open || typeof document === 'undefined') return null;

  return createPortal(
    <div
      className="tf-modal-backdrop fixed inset-0 z-[140] flex items-center justify-center bg-black/60 p-4 backdrop-blur-sm"
      role="presentation"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget && !busy) onCancel?.();
      }}
    >
      <section
        className="w-full max-w-lg rounded-2xl border border-red-300 bg-white p-5 shadow-2xl dark:border-red-900/80 dark:bg-neutral-950"
        role="dialog"
        aria-modal="true"
        aria-labelledby="course-delete-dialog-title"
        aria-describedby="course-delete-dialog-description"
        data-prevent-escape-close="true"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="flex items-start gap-3">
          <div className="mt-0.5 rounded-xl bg-red-100 p-2 text-red-700 dark:bg-red-950/70 dark:text-red-300">
            <AlertTriangle size={20} aria-hidden="true" />
          </div>
          <div className="min-w-0 flex-1">
            <h2 id="course-delete-dialog-title" className="text-lg font-semibold text-red-700 dark:text-red-300">Подтвердите удаление курса</h2>
            <p id="course-delete-dialog-description" className="mt-1 text-sm text-neutral-600 dark:text-neutral-300">
              Это необратимое действие. Вместе с курсом будут удалены все вложенные подкурсы. Чтобы случайный клик не удалил дерево курса, нужно подтвердить его название вручную.
            </p>
          </div>
          <button
            type="button"
            className="rounded-lg p-1.5 text-neutral-500 transition hover:bg-neutral-100 hover:text-neutral-900 disabled:opacity-40 dark:hover:bg-neutral-800 dark:hover:text-white"
            onClick={() => onCancel?.()}
            disabled={busy}
            aria-label="Закрыть подтверждение"
          >
            <X size={18} />
          </button>
        </div>

        <div className="mt-5 rounded-xl border border-red-200 bg-red-50/80 p-3 dark:border-red-900/70 dark:bg-red-950/30">
          <div className="text-xs font-medium uppercase tracking-wide text-red-700/80 dark:text-red-300/80">Курс</div>
          <div className="mt-1 break-words font-semibold text-neutral-900 dark:text-neutral-100">{expectedTitle}</div>
        </div>

        <label className="mt-5 block text-sm font-medium text-neutral-800 dark:text-neutral-200" htmlFor="course-delete-confirm-title">
          Введите название курса полностью
        </label>
        <input
          ref={inputRef}
          id="course-delete-confirm-title"
          className="input mt-2"
          value={typedTitle}
          onChange={(event) => setTypedTitle(event.target.value)}
          autoComplete="off"
          spellCheck={false}
          disabled={busy}
          placeholder={expectedTitle}
        />
        {typedTitle && normalizedCourseDeleteTitle(typedTitle) !== expectedTitle ? (
          <div className="mt-2 text-xs text-red-600 dark:text-red-300">Название не совпадает.</div>
        ) : null}

        <label className="mt-4 flex cursor-pointer items-start gap-3 rounded-xl border border-neutral-200 p-3 text-sm text-neutral-700 dark:border-neutral-800 dark:text-neutral-200">
          <input
            type="checkbox"
            className="mt-0.5 h-4 w-4"
            checked={acknowledged}
            onChange={(event) => setAcknowledged(event.target.checked)}
            disabled={busy}
          />
          <span>Я понимаю, что удаляю этот курс вместе со всеми вложенными подкурсами и отменить удаление после выполнения нельзя.</span>
        </label>

        <div className="mt-5 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <Button type="button" variant="outline" onClick={() => onCancel?.()} disabled={busy}>Отмена</Button>
          <button
            type="button"
            className="inline-flex min-h-10 items-center justify-center gap-2 rounded-xl border border-red-700 bg-red-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-red-700 disabled:cursor-not-allowed disabled:opacity-40"
            onClick={() => canDelete && onConfirm?.()}
            disabled={!canDelete}
          >
            <Trash2 size={16} /> {busy ? 'Удаляю…' : 'Удалить курс навсегда'}
          </button>
        </div>
      </section>
    </div>,
    document.body,
  );
}
