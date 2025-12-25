// clientapp/src/components/ui/index.js
// Мини-набор UI компонентов для проекта.
// Важно: в проекте уже есть классы в src/index.css (btn-*, input, card и т.д.)
// поэтому тут стараемся использовать именно их, а не "form-*" классы.

import React from 'react';
import clsx from 'clsx';

export function Field({ label, hint, children, className }) {
  return (
    <div className={clsx('space-y-1', className)}>
      {label && <label className="font-medium text-sm block">{label}</label>}
      {children}
      {hint && <div className="text-xs text-slate-500 dark:text-slate-400">{hint}</div>}
    </div>
  );
}

export function Input({ className, ...props }) {
  return <input className={clsx('input', className)} {...props} />;
}

export function Textarea({ className, ...props }) {
  return <textarea className={clsx('input', className)} {...props} />;
}

export function Select({ className, ...props }) {
  // В index.css стилизуем select и select.input
  return <select className={clsx('input', className)} {...props} />;
}

export function Button({
  variant,
  intent, // поддержка старого API страниц (intent вместо variant)
  size,
  className,
  children,
  ...rest
}) {
  const v = variant ?? intent ?? 'primary';

  const base =
    v === 'outline' || v === 'secondary'
      ? 'btn-outline'
      : v === 'ghost'
      ? 'btn-ghost'
      : v === 'danger'
      ? 'btn bg-red-600 text-white hover:bg-red-700 active:scale-[.99]'
      : v === 'success'
      ? 'btn bg-emerald-600 text-white hover:bg-emerald-700 active:scale-[.99]'
      : 'btn-primary';

  const sz =
    size === 'sm'
      ? 'text-sm px-2 py-1 rounded-lg'
      : size === 'lg'
      ? 'text-base px-4 py-2.5 rounded-2xl'
      : null;

  return (
    <button className={clsx(base, sz, className)} {...rest}>
      {children}
    </button>
  );
}

export function Card({ className, children, ...rest }) {
  return (
    <div
      className={clsx(
        'rounded-xl border border-slate-200 dark:border-slate-700 bg-[rgb(var(--card))] p-4 shadow-soft',
        className
      )}
      {...rest}
    >
      {children}
    </div>
  );
}

export function Badge({ intent = 'default', className, children, ...rest }) {
  const map = {
    default:
      'bg-slate-100 text-slate-700 border-slate-200 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700',
    success:
      'bg-emerald-100 text-emerald-700 border-emerald-200 dark:bg-emerald-900/40 dark:text-emerald-200 dark:border-emerald-800',
    danger:
      'bg-red-100 text-red-700 border-red-200 dark:bg-red-900/40 dark:text-red-200 dark:border-red-800',
    warning:
      'bg-amber-100 text-amber-800 border-amber-200 dark:bg-amber-900/40 dark:text-amber-200 dark:border-amber-800',
  };

  return (
    <span
      className={clsx(
        'inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium',
        map[intent] ?? map.default,
        className
      )}
      {...rest}
    >
      {children}
    </span>
  );
}
