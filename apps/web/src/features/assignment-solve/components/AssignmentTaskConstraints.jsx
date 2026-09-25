import React from 'react';

export default function AssignmentTaskConstraints({ constraints }) {
  const required = Array.isArray(constraints?.required) ? constraints.required.filter(Boolean) : [];
  const forbidden = Array.isArray(constraints?.forbidden) ? constraints.forbidden.filter(Boolean) : [];

  if (required.length === 0 && forbidden.length === 0) return null;

  return (
    <div
      className="solve-constraints mt-4 p-3 text-sm"
      data-taskforge-automation-id="assignment-solution-requirements"
      data-taskforge-agent-role="assignment-solution-requirements"
    >
      <div className="solve-constraints-title font-semibold">Требования к решению</div>
      <div className="solve-muted-copy mt-1 text-xs">
        Это учебные правила конкретного задания. Они проверяются до запуска программы.
      </div>

      {required.length > 0 ? (
        <div className="mt-3">
          <div className="solve-muted-copy mb-1 text-xs font-medium">Нужно использовать</div>
          <div className="flex flex-wrap gap-1.5">
            {required.map((rule) => (
              <code key={`required:${rule}`} className="solve-constraint-chip is-required px-2 py-1 text-xs">
                {rule}
              </code>
            ))}
          </div>
        </div>
      ) : null}

      {forbidden.length > 0 ? (
        <div className="mt-3">
          <div className="solve-muted-copy mb-1 text-xs font-medium">Нельзя использовать в этом задании</div>
          <div className="flex flex-wrap gap-1.5">
            {forbidden.map((rule) => (
              <code key={`forbidden:${rule}`} className="solve-constraint-chip is-forbidden px-2 py-1 text-xs">
                {rule}
              </code>
            ))}
          </div>
        </div>
      ) : null}
    </div>
  );
}
