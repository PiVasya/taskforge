import React, { useState } from "react";

export default function RuntimeErrorPanel({ run, source }) {
  if (!run || (run.exitCode === 0 && !run.exception)) return null;
  const [showRaw, setShowRaw] = useState(false);

  const line = run.exception?.line;
  const renderSnippet = () => {
    if (!source || !line) return null;
    const lines = source.split(/\r?\n/);
    const idx = Math.max(0, Math.min(lines.length - 1, line - 1));
    const from = Math.max(0, idx - 2);
    const to = Math.min(lines.length - 1, idx + 2);
    return (
      <pre className="mt-3 text-sm bg-slate-900 text-slate-100 rounded-xl p-3 overflow-auto">
        {lines.slice(from, to + 1).map((ln, i) => {
          const no = from + i + 1;
          const highlight = no === line ? "bg-amber-500/20" : "";
          return (
            <div key={no} className={`flex ${highlight}`}>
              <span className="w-12 text-right pr-3 opacity-60 select-none">{no}</span>
              <code className="whitespace-pre-wrap break-words">{ln}</code>
            </div>
          );
        })}
      </pre>
    );
  };

  return (
    <div className="rounded-2xl border border-amber-300 bg-amber-50 p-4 dark:bg-amber-950/20 dark:border-amber-700">
      <div className="flex items-center justify-between">
        <div className="font-semibold text-amber-900 dark:text-amber-100">Исключение во время выполнения</div>
        <button
          type="button"
          onClick={() => setShowRaw((v) => !v)}
          className="text-xs underline text-amber-800/80"
        >
          {showRaw ? "Скрыть stderr" : "Показать stderr"}
        </button>
      </div>

      {run.exception ? (
        <div className="mt-1 text-sm text-amber-900 dark:text-amber-100">
          <div className="font-mono">{run.exception.type}</div>
          <div className="whitespace-pre-wrap">{run.exception.message}</div>
        </div>
      ) : null}

      {renderSnippet()}

      {showRaw && run.stderr && (
        <pre className="mt-3 text-xs bg-white/70 dark:bg-slate-900/30 rounded p-3 whitespace-pre-wrap overflow-auto">
          {run.stderr}
        </pre>
      )}
    </div>
  );
}
