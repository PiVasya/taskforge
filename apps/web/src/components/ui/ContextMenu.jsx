import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Check } from 'lucide-react';
import { createPortal } from 'react-dom';

function getEnabledItems(menu) {
  if (!menu) return [];
  return Array.from(menu.querySelectorAll('[role="menuitem"]')).filter((item) => !item.disabled && item.getAttribute('aria-disabled') !== 'true');
}

export function ContextMenu({ open, x = 0, y = 0, onClose, children, ariaLabel = 'Контекстное меню', minWidth = 240 }) {
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

    const frame = window.requestAnimationFrame(() => {
      getEnabledItems(menu)[0]?.focus({ preventScroll: true });
    });
    return () => window.cancelAnimationFrame(frame);
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
      className="tf-context-menu fixed z-[12000] max-w-[min(360px,calc(100vw-16px))] overflow-hidden rounded-xl border border-[rgba(var(--border)/0.8)] bg-[rgb(var(--card))] p-1.5 shadow-2xl"
      style={{ left: position.x, top: position.y, minWidth }}
      onContextMenu={(event) => event.preventDefault()}
      onKeyDown={(event) => {
        const items = getEnabledItems(menuRef.current);
        if (!items.length) return;
        const currentIndex = items.indexOf(document.activeElement);
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
          event.preventDefault();
          const delta = event.key === 'ArrowDown' ? 1 : -1;
          const nextIndex = currentIndex < 0
            ? (delta > 0 ? 0 : items.length - 1)
            : (currentIndex + delta + items.length) % items.length;
          items[nextIndex]?.focus();
        } else if (event.key === 'Home' || event.key === 'End') {
          event.preventDefault();
          items[event.key === 'Home' ? 0 : items.length - 1]?.focus();
        }
      }}
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

export function ContextMenuItem({
  icon: Icon,
  children,
  description = '',
  shortcut = '',
  checked = false,
  onClick,
  disabled = false,
  danger = false,
}) {
  return (
    <button
      type="button"
      role="menuitem"
      disabled={disabled}
      onClick={onClick}
      className={`tf-context-menu-item flex w-full items-center gap-2.5 rounded-lg px-3 py-2 text-left text-sm transition focus:outline-none focus-visible:ring-2 focus-visible:ring-[rgba(var(--accent)/0.45)] disabled:cursor-not-allowed disabled:opacity-45 ${danger ? 'text-red-600 dark:text-red-400' : 'text-[rgb(var(--text))]'} hover:bg-[rgba(var(--muted)/0.8)] focus:bg-[rgba(var(--muted)/0.8)]`}
    >
      <span className="flex h-4 w-4 shrink-0 items-center justify-center">
        {checked ? <Check size={15} strokeWidth={2.5} /> : Icon ? <Icon size={16} /> : null}
      </span>
      <span className="min-w-0 flex-1">
        <span className="block truncate">{children}</span>
        {description ? <span className="mt-0.5 block max-w-[250px] whitespace-normal text-[10px] leading-4 text-neutral-500">{description}</span> : null}
      </span>
      {shortcut ? <kbd className="ml-3 shrink-0 rounded border border-[rgba(var(--border)/0.7)] bg-[rgba(var(--muted)/0.45)] px-1.5 py-0.5 text-[10px] font-medium text-neutral-500">{shortcut}</kbd> : null}
    </button>
  );
}
