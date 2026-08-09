import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

export function ContextMenu({ open, x = 0, y = 0, onClose, children, ariaLabel = 'Контекстное меню' }) {
  const menuRef = useRef(null);
  const [position, setPosition] = useState({ x, y });

  useLayoutEffect(() => {
    if (!open) return;
    const menu = menuRef.current;
    if (!menu) return;

    const margin = 8;
    const rect = menu.getBoundingClientRect();
    const maxX = Math.max(margin, window.innerWidth - rect.width - margin);
    const maxY = Math.max(margin, window.innerHeight - rect.height - margin);
    setPosition({
      x: Math.min(Math.max(margin, x), maxX),
      y: Math.min(Math.max(margin, y), maxY),
    });
  }, [open, x, y]);

  useEffect(() => {
    if (!open) return undefined;

    const closeOutside = (event) => {
      if (!menuRef.current?.contains(event.target)) onClose?.();
    };
    const closeOnEscape = (event) => {
      if (event.key === 'Escape') onClose?.();
    };
    const close = () => onClose?.();

    document.addEventListener('pointerdown', closeOutside, true);
    document.addEventListener('keydown', closeOnEscape);
    window.addEventListener('resize', close);
    window.addEventListener('scroll', close, true);

    return () => {
      document.removeEventListener('pointerdown', closeOutside, true);
      document.removeEventListener('keydown', closeOnEscape);
      window.removeEventListener('resize', close);
      window.removeEventListener('scroll', close, true);
    };
  }, [open, onClose]);

  if (!open || typeof document === 'undefined') return null;

  return createPortal(
    <div
      ref={menuRef}
      role="menu"
      aria-label={ariaLabel}
      className="tf-context-menu fixed z-[12000] min-w-[240px] overflow-hidden rounded-xl border border-[rgba(var(--border)/0.8)] bg-[rgb(var(--card))] p-1.5 shadow-2xl"
      style={{ left: position.x, top: position.y }}
      onContextMenu={(event) => event.preventDefault()}
    >
      {children}
    </div>,
    document.body,
  );
}

export function ContextMenuLabel({ children }) {
  return <div className="px-3 pb-1 pt-1.5 text-[11px] font-semibold uppercase tracking-[0.12em] text-neutral-500">{children}</div>;
}

export function ContextMenuSeparator() {
  return <div className="my-1 h-px bg-[rgba(var(--border)/0.55)]" />;
}

export function ContextMenuItem({ icon: Icon, children, onClick, disabled = false, danger = false }) {
  return (
    <button
      type="button"
      role="menuitem"
      disabled={disabled}
      onClick={onClick}
      className={`tf-context-menu-item flex w-full items-center gap-2.5 px-3 py-2 text-left text-sm transition disabled:cursor-not-allowed disabled:opacity-45 ${danger ? 'text-red-600 dark:text-red-400' : 'text-[rgb(var(--text))]'} hover:bg-[rgba(var(--muted)/0.8)]`}
    >
      {Icon ? <Icon size={16} className="shrink-0" /> : null}
      <span className="min-w-0 flex-1">{children}</span>
    </button>
  );
}
