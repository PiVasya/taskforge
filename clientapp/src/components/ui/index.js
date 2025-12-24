// clientapp/src/components/ui/index.js
// Набор простых UI компонентов (Field, Input, Textarea, Select, Button, Card).
// Эти компоненты являются упрощённой версией настоящих UI-элементов, использующих
// Tailwind CSS классы. В реальном проекте используйте существующие
// компоненты из библиотеки.

import React from 'react';

export function Field({ label, hint, children }) {
  return (
    <div className="space-y-1">
      {label && <label className="font-medium text-sm block">{label}</label>}
      {children}
      {hint && <div className="text-xs text-slate-500 dark:text-slate-400">{hint}</div>}
    </div>
  );
}

export function Input(props) {
  return <input className="form-input w-full" {...props} />;
}

export function Textarea(props) {
  return <textarea className="form-textarea w-full" {...props} />;
}

export function Select(props) {
  return <select className="form-select w-full" {...props} />;
}

export function Button({ variant = 'primary', children, ...rest }) {
  const base =
    variant === 'outline'
      ? 'btn-outline'
      : variant === 'ghost'
      ? 'btn-ghost'
      : 'btn-primary';
  return (
    <button className={base} {...rest}>
      {children}
    </button>
  );
}

export function Card({ children }) {
  return (
    <div className="rounded-xl border border-slate-200 dark:border-slate-700 bg-[rgb(var(--card))] p-4 shadow-soft">
      {children}
    </div>
  );
}