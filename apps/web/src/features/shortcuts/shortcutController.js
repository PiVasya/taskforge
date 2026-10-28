import { SHORTCUTS, navigationShortcuts, shortcutAllowed } from './shortcutCatalog';

const CONTROL = '[data-tf-shortcut]';
// Some legacy modals use the shared backdrop class rather than ARIA.
const MODAL = '[aria-modal="true"],[role="dialog"],[role="alertdialog"],dialog[open],.tf-modal-backdrop,[data-shortcut-modal],.fixed.inset-0[class*="bg-black"]';

export function isRendered(element) {
  if (!element?.isConnected || element.closest('[hidden],[inert],[aria-hidden="true"]')) return false;
  const style = element.ownerDocument.defaultView.getComputedStyle(element);
  return style.display !== 'none' && style.visibility !== 'hidden' && element.getClientRects().length > 0;
}

export function topShortcutModal(doc, visible = isRendered) {
  const modals = [...doc.querySelectorAll(MODAL)].filter(visible);
  // Nested panels belong to the same modal. The panel is the narrowest scope.
  const leaves = modals.filter((node) => !modals.some((other) => other !== node && node.contains(other)));
  const layer = (node) => {
    let highest = 0;
    for (let current = node; current; current = current.parentElement) {
      highest = Math.max(highest, Number.parseInt(doc.defaultView.getComputedStyle(current).zIndex, 10) || 0);
    }
    return highest;
  };
  return leaves.map((node, order) => ({ node, order, layer: layer(node) }))
    .sort((a, b) => a.layer - b.layer || a.order - b.order).at(-1)?.node || null;
}

export function safeShortcutHref(value, origin) {
  if (!value || typeof value !== 'string') return null;
  try {
    const url = new URL(value, origin);
    if (url.origin !== origin || !['https:', 'http:'].includes(url.protocol)) return null;
    return `${url.pathname}${url.search}${url.hash}`;
  } catch { return null; }
}

export function eventKeyCode(event) {
  if (event.code) return event.code === 'NumpadEnter' ? 'Enter' : event.code;
  if (/^[a-z]$/i.test(event.key || '')) return `Key${event.key.toUpperCase()}`;
  if (/^[0-9]$/.test(event.key || '')) return `Digit${event.key}`;
  return ({ '/': 'Slash', '`': 'Backquote', '.': 'Period', ',': 'Comma', '-': 'Minus' })[event.key] || event.key;
}

const blocked = (element) => element.disabled || element.matches(':disabled')
  || element.closest('[aria-disabled="true"],[aria-busy="true"]');
const hrefOf = (element, origin) => safeShortcutHref(element?.getAttribute('href') || element?.dataset.tfShortcutHref, origin);
const consume = (event) => { event.preventDefault(); event.stopPropagation(); event.stopImmediatePropagation?.(); };

/** One capture listener dispatches to the existing controls, never to a second API path. */
export function createShortcutController({
  doc = document, win = window, getState, navigate, openHelp, clearPinned,
  visible = isRendered, openTab = (href) => win.open(href, '_blank', 'noopener,noreferrer'),
}) {
  let altHeld = false;
  let ctrlHeld = false;
  let rightAlt = false;
  let frame = 0;
  let activeScope = null;
  const pressed = new Set();
  const lastRun = new Map();
  const root = doc.documentElement;

  const refresh = () => {
    const state = getState();
    const showing = state.enabled !== false && state.hints !== false && (altHeld || state.pinned);
    root.dataset.tfShortcuts = showing ? (ctrlHeld ? 'new-tab' : 'on') : 'off';
    const modal = showing ? topShortcutModal(doc, visible) : null;
    if (activeScope !== modal) {
      activeScope?.removeAttribute('data-tf-shortcut-scope');
      activeScope = modal;
      activeScope?.setAttribute('data-tf-shortcut-scope', 'active');
    }
    root.dataset.tfShortcutModal = modal ? 'true' : 'false';
  };
  const scheduleRefresh = () => {
    if (frame || !(altHeld || getState().pinned)) return;
    frame = win.requestAnimationFrame(() => { frame = 0; refresh(); });
  };
  const reset = () => {
    altHeld = false; ctrlHeld = false; rightAlt = false; pressed.clear();
    clearPinned?.();
    refresh();
    root.dataset.tfShortcuts = 'off';
  };

  const keydown = (event) => {
    const state = getState();
    if (event.defaultPrevented) return;
    if (event.code === 'AltRight') { rightAlt = true; altHeld = false; refresh(); return; }
    if (event.isComposing || event.keyCode === 229 || event.getModifierState?.('AltGraph') || rightAlt) {
      altHeld = false; refresh(); return;
    }
    if (state.enabled === false) return;
    if (event.key === 'Escape' && state.pinned) { clearPinned?.(); altHeld = false; refresh(); return; }
    if (event.key === 'Alt' || event.code === 'AltLeft') {
      altHeld = true; ctrlHeld = event.ctrlKey; refresh();
      // Prevent the menu bar from taking focus after a bare Alt release.
      event.preventDefault();
      return;
    }
    if (event.key === 'Control') { ctrlHeld = true; refresh(); return; }
    if (!event.altKey || event.metaKey) return;
    altHeld = true; ctrlHeld = event.ctrlKey; refresh();
    const code = eventKeyCode(event);
    const modal = topShortcutModal(doc, visible);
    const scope = modal || doc;
    const origin = win.location.origin;
    const focused = doc.activeElement;
    const inside = (element) => !modal || modal.contains(element);

    // Ctrl+Alt+Enter operates only on an actually focused link, not on a submit button.
    if (event.ctrlKey && !event.shiftKey && code === 'Enter') {
      const link = focused?.closest?.('a[href],[data-tf-shortcut-href]');
      const href = link && inside(link) && visible(link) && !blocked(link) ? hrefOf(link, origin) : null;
      consume(event);
      if (href && !event.repeat && !pressed.has(code)) { pressed.add(code); openTab(href); }
      return;
    }
    const matches = (id) => {
      const item = SHORTCUTS[id];
      return item && item.code === code && Boolean(item.shift) === event.shiftKey && shortcutAllowed(item, state);
    };
    if (matches('help')) {
      consume(event);
      if (!event.ctrlKey && !event.repeat && !pressed.has(code)) { pressed.add(code); openHelp(); }
      return;
    }
    const candidates = [...scope.querySelectorAll(CONTROL)]
      .filter((node) => matches(node.dataset.tfShortcut) && visible(node));
    const focusedScope = focused?.closest?.('[data-hotkey-context]');
    const contextual = focusedScope ? candidates.filter((node) => focusedScope.contains(node)) : [];
    const ranked = contextual.length ? contextual : candidates;
    const priority = Math.max(0, ...ranked.map((node) => Number(node.dataset.tfShortcutPriority) || 0));
    const list = ranked.filter((node) => (Number(node.dataset.tfShortcutPriority) || 0) === priority);
    const targeted = list.filter((node) => node === focused || node.contains(focused));
    // Ambiguous repeated row actions must not silently operate on the first account.
    const unambiguous = list.length === 1 || (list.length > 1 && list.every((node) => hrefOf(node, origin) && hrefOf(node, origin) === hrefOf(list[0], origin)));
    const element = targeted[0] || (unambiguous ? list[0] : null);
    const navigation = !modal ? navigationShortcuts(state).find((item) => matches(item.id)) : null;
    const actionId = !modal ? Object.keys(state.actions || {}).find(matches) : null;
    if (!element && !navigation && !actionId) {
      if (list.length > 0 || (modal && code === 'Enter')) consume(event);
      return;
    }
    consume(event);
    if (event.repeat || pressed.has(code)) return;
    pressed.add(code);
    const id = element?.dataset.tfShortcut || navigation?.id || actionId;
    if (Date.now() - (lastRun.get(id) || 0) < 350) return;
    if (element && blocked(element)) return;
    const href = element ? hrefOf(element, origin) : navigation?.href;
    if (event.ctrlKey) {
      if (href) { lastRun.set(id, Date.now()); openTab(href); }
      return;
    }
    lastRun.set(id, Date.now());
    if (element?.dataset.tfShortcutFocus === 'true') element.focus({ preventScroll: false });
    else if (element) element.click();
    else if (href) navigate(href);
    else state.actions[actionId]();
    scheduleRefresh();
  };
  const keyup = (event) => {
    pressed.delete(eventKeyCode(event));
    if (event.code === 'AltRight') rightAlt = false;
    if (event.key === 'Alt' || event.code === 'AltLeft' || !event.altKey) {
      if (altHeld && getState().enabled !== false) event.preventDefault();
      altHeld = false;
    }
    ctrlHeld = event.ctrlKey;
    refresh();
  };
  const visibility = () => { if (doc.hidden) reset(); };
  win.addEventListener('keydown', keydown, true);
  win.addEventListener('keyup', keyup, true);
  win.addEventListener('blur', reset);
  win.addEventListener('pagehide', reset);
  doc.addEventListener('visibilitychange', visibility);
  const observer = new win.MutationObserver(scheduleRefresh);
  observer.observe(doc.body, { childList: true, subtree: true });
  refresh();
  return {
    refresh, reset,
    dispose() {
      win.removeEventListener('keydown', keydown, true);
      win.removeEventListener('keyup', keyup, true);
      win.removeEventListener('blur', reset);
      win.removeEventListener('pagehide', reset);
      doc.removeEventListener('visibilitychange', visibility);
      observer.disconnect();
      if (frame) win.cancelAnimationFrame(frame);
      activeScope?.removeAttribute('data-tf-shortcut-scope');
      delete root.dataset.tfShortcuts;
      delete root.dataset.tfShortcutModal;
    },
  };
}
