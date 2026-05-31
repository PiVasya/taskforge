import React from 'react';
import { AlertCircle, Info, ListChecks } from 'lucide-react';
import { getApiErrorMessage } from '../api/http';

function normalize(error) {
  if (!error) return null;
  if (typeof error === 'string') {
    const msg = getApiErrorMessage({ message: error }, 'Не удалось выполнить действие');
    return { primaryMessage: msg, messages: [msg], severity: 'error' };
  }
  return error;
}

export default function AppErrorPanel({ error, title = 'Не удалось выполнить действие', compact = false }) {
  const e = normalize(error);
  if (!e) return null;

  const messages = Array.isArray(e.messages) ? e.messages.filter(Boolean) : [];
  const hasExtraMessages = messages.length > 1;
  const howToFix = Array.isArray(e.howToFix) ? e.howToFix.filter(Boolean) : [];
  const severity = e.severity || 'error';

  const tone = severity === 'warning'
    ? 'border-amber-200 bg-amber-50 text-amber-900 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-100'
    : severity === 'validation'
      ? 'border-orange-200 bg-orange-50 text-orange-900 dark:border-orange-900/40 dark:bg-orange-950/30 dark:text-orange-100'
      : 'border-red-200 bg-red-50 text-red-900 dark:border-red-900/40 dark:bg-red-950/30 dark:text-red-100';

  return (
    <div className={`rounded-2xl border px-4 py-3 ${tone}`}>
      <div className="flex items-start gap-3">
        <AlertCircle size={18} className="mt-0.5 shrink-0" />
        <div className="min-w-0 flex-1 space-y-2">
          <div>
            <div className="font-semibold">{title}</div>
            <div className="text-sm whitespace-pre-wrap">{e.primaryMessage || messages[0] || 'Не удалось выполнить действие'}</div>
          </div>

          {e.userHint ? (
            <div className="rounded-xl bg-white/50 dark:bg-black/10 px-3 py-2 text-sm">
              <div className="flex items-center gap-2 font-medium"><Info size={15} /> Что это значит</div>
              <div className="mt-1 whitespace-pre-wrap">{e.userHint}</div>
            </div>
          ) : null}

          {howToFix.length > 0 ? (
            <div className="rounded-xl bg-white/50 dark:bg-black/10 px-3 py-2 text-sm">
              <div className="flex items-center gap-2 font-medium"><ListChecks size={15} /> Что можно сделать</div>
              <ol className="mt-1 list-decimal pl-5 space-y-1">
                {howToFix.map((step, idx) => <li key={`${idx}-${step}`}>{step}</li>)}
              </ol>
            </div>
          ) : null}

          {!compact && hasExtraMessages ? (
            <div className="text-sm">
              <div className="font-medium">Подробности</div>
              <ul className="mt-1 list-disc pl-5 space-y-1">
                {messages.slice(1).map((msg, idx) => <li key={`${idx}-${msg}`}>{msg}</li>)}
              </ul>
            </div>
          ) : null}

          {(e.code || e.traceId || e.path) ? (
            <div className="text-xs opacity-80 break-words">
              {e.code ? <span>Код: {e.code}</span> : null}
              {e.code && (e.traceId || e.path) ? <span> · </span> : null}
              {e.traceId ? <span>Trace: {e.traceId}</span> : null}
              {e.traceId && e.path ? <span> · </span> : null}
              {e.path ? <span>Путь: {e.path}</span> : null}
            </div>
          ) : null}
        </div>
      </div>
    </div>
  );
}
