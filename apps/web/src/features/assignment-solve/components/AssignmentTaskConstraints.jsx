import React from 'react';

export default function AssignmentTaskConstraints({ constraints }) {
  const required = Array.isArray(constraints?.required) ? constraints.required.filter(Boolean) : [];
  const forbidden = Array.isArray(constraints?.forbidden) ? constraints.forbidden.filter(Boolean) : [];

  if (required.length === 0 && forbidden.length === 0) return null;

  return (
    <div
      className="mt-4 rounded-xl border border-amber-400/25 bg-amber-500/5 p-3 text-sm"
      data-taskforge-automation-id="assignment-solution-requirements"
      data-taskforge-agent-role="assignment-solution-requirements"
    >
      <div className="font-semibold text-amber-200">Требования к решению</div>
      <div className="mt-1 text-xs text-neutral-400">
        Это учебные правила конкретного задания. Они проверяются до запуска программы.
      </div>

      {required.length > 0 ? (
        <div className="mt-3">
          <div className="mb-1 text-xs font-medium text-neutral-400">Нужно использовать</div>
          <div className="flex flex-wrap gap-1.5">
            {required.map((rule) => (
              <code key={`required:${rule}`} className="rounded-md border border-emerald-400/25 bg-emerald-500/10 px-2 py-1 text-xs text-emerald-200">
                {rule}
              </code>
            ))}
          </div>
        </div>
      ) : null}

      {forbidden.length > 0 ? (
        <div className="mt-3">
          <div className="mb-1 text-xs font-medium text-neutral-400">Нельзя использовать в этом задании</div>
          <div className="flex flex-wrap gap-1.5">
            {forbidden.map((rule) => (
              <code key={`forbidden:${rule}`} className="rounded-md border border-rose-400/25 bg-rose-500/10 px-2 py-1 text-xs text-rose-200">
                {rule}
              </code>
            ))}
          </div>
        </div>
      ) : null}
    </div>
  );
}
