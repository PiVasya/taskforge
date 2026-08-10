import { useEffect, useRef } from 'react';

/**
 * Consistent Ctrl/Cmd+S handling for TaskForge editors.
 *
 * The handler runs in capture phase so browser/editor widgets (Monaco, Tiptap,
 * inputs) cannot accidentally open the browser "Save page" dialog first.
 * Callers decide when saving is enabled; disabled shortcuts still prevent the
 * browser dialog only when `preventBrowserSave` is true.
 */
export default function useSaveShortcut(onSave, {
  enabled = true,
  busy = false,
  preventBrowserSave = true,
  shouldHandle = null,
} = {}) {
  const saveRef = useRef(onSave);
  const stateRef = useRef({ enabled, busy, preventBrowserSave, shouldHandle });

  useEffect(() => { saveRef.current = onSave; }, [onSave]);
  useEffect(() => {
    stateRef.current = { enabled, busy, preventBrowserSave, shouldHandle };
  }, [enabled, busy, preventBrowserSave, shouldHandle]);

  useEffect(() => {
    const handleKeyDown = (event) => {
      const mod = event.ctrlKey || event.metaKey;
      if (!mod || event.altKey || String(event.key || '').toLowerCase() !== 's') return;

      const state = stateRef.current;
      if (typeof state.shouldHandle === 'function' && !state.shouldHandle(event)) return;
      if (state.preventBrowserSave) event.preventDefault();
      if (!state.enabled || state.busy || event.repeat) return;

      event.stopPropagation();
      const result = saveRef.current?.();
      if (result && typeof result.catch === 'function') result.catch(() => {});
    };

    window.addEventListener('keydown', handleKeyDown, true);
    return () => window.removeEventListener('keydown', handleKeyDown, true);
  }, []);
}
