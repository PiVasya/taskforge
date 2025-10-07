import React, { useState } from "react";

export default function CompileErrorPanel({ compile, source }) {
  if (!compile || compile.ok) return null;
  const [showRaw, setShowRaw] = useState(false);
  const first = compile.diagnostics?.[0];

  const renderSnippet = (lineNum) => {
    if (!source || !lineNum) return null;
    const lines = source.split(/\r?\n/);
    const idx = Math.max(0, Math.min(lines.length - 1, lineNum - 1));
    const from = Math.max(0, idx - 2);
    const to = Math.min(lines.length - 1, idx + 2);
    return (
      <pre className="mt-3 text-sm bg-slate-900 text-slate-100 rounded-xl p-3 overflow-auto">
        {lines.slice(from, to + 1).map((ln, i) => {
          const no = from + i + 1;
          const highlight = no === lineNum ? "bg-red-600/20" : "";
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
    <div className="rounded-2xl border border-red-300 bg-red-50 p-4 dark:bg-red-950/20 dark:border-red-700">
      <div className="flex items-center justify-between">
        <div className="font-semibold text-red-700 dark:text-red-200">Ошибка компиляции</div>
        <button
          type="button"
          onClick={() => setShowRaw((v) => !v)}
          className="text-xs underline text-red-700/80"
        >
          {showRaw ? "Скрыть сырой лог" : "Показать сырой лог"}
        </button>
      </div>

      {compile.diagnostics?.length ? (
        <ul className="mt-2 text-sm text-red-700 dark:text-red-200 list-disc pl-5">
          {compile.diagnostics.map((d, idx) => (
            <li key={idx}>
              {d.code ? <span className="font-mono">{d.code}</span> : null} {d.message}
              {d.line ? ` (строка ${d.line}${d.column ? `:${d.column}` : ""})` : null}
            </li>
          ))}
        </ul>
      ) : null}

      {first?.line ? renderSnippet(first.line) : null}

      {showRaw && compile.stderr && (
        <pre className="mt-3 text-xs bg-white/70 dark:bg-slate-900/30 rounded p-3 whitespace-pre-wrap overflow-auto">
          {compile.stderr}
        </pre>
      )}
    </div>
  );
}
