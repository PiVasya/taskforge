import test from 'node:test';
import assert from 'node:assert/strict';
import { loadSourceModule } from '../test-utils/load-source-module.mjs';

const { createShortcutController } = await loadSourceModule(new URL('../../src/features/shortcuts/shortcutController.js', import.meta.url));

function harness() {
  const callbacks = new Map();
  const hits = { nav: [], tabs: [], actions: [], help: 0 };
  const primary = {
    dataset: { tfShortcut: 'primary' },
    disabled: false,
    matches: () => false,
    closest: () => null,
    contains: () => false,
    getAttribute: () => null,
    click: () => hits.actions.push('submit'),
  };
  const doc = {
    documentElement: { dataset: {} },
    activeElement: { closest: () => null },
    body: {}, hidden: false,
    querySelectorAll: (selector) => selector === '[data-tf-shortcut]' ? [primary] : [],
    addEventListener: () => {}, removeEventListener: () => {},
  };
  const win = {
    location: { origin: 'https://taskforge.test' },
    addEventListener: (kind, callback) => callbacks.set(kind, callback),
    removeEventListener: (kind) => callbacks.delete(kind),
    requestAnimationFrame: (callback) => { callback(); return 1; },
    cancelAnimationFrame: () => {},
    MutationObserver: class { observe() {} disconnect() {} },
  };
  const state = { enabled: true, hints: true, authenticated: true, admin: true, editor: true, minecraft: true, pinned: false };
  const controller = createShortcutController({
    win, doc, getState: () => state, visible: () => true,
    openHelp: () => { hits.help += 1; },
    navigate: (href) => hits.nav.push(href),
    openTab: (href) => hits.tabs.push(href),
    clearPinned: () => { state.pinned = false; },
  });
  const key = (code, props = {}, type = 'keydown') => {
    const event = {
      code, key: code.startsWith('Alt') ? 'Alt' : code.startsWith('Control') ? 'Control' : code.replace(/^Key/, ''),
      altKey: true, ctrlKey: false, shiftKey: false, metaKey: false, repeat: false,
      defaultPrevented: false, preventDefault() { this.defaultPrevented = true; },
      stopPropagation() {}, stopImmediatePropagation() {},
      ...props,
    };
    callbacks.get(type)?.(event);
    return event;
  };
  return { controller, doc, state, hits, key };
}

test('AltRight shows hints and dispatches navigation, primary action and help', () => {
  const h = harness();
  h.key('AltRight');
  assert.equal(h.doc.documentElement.dataset.tfShortcuts, 'on');
  const c = h.key('KeyC', { ctrlKey: true, altKey: false });
  assert.equal(c.defaultPrevented, true);
  assert.deepEqual(h.hits.nav, ['/courses']);
  assert.deepEqual(h.hits.tabs, []);
  h.key('KeyC', { ctrlKey: true, altKey: false }, 'keyup');
  h.key('Enter');
  assert.deepEqual(h.hits.actions, ['submit']);
  h.key('Slash');
  assert.equal(h.hits.help, 1);
  h.key('AltRight', { altKey: false }, 'keyup');
  assert.equal(h.doc.documentElement.dataset.tfShortcuts, 'off');
  assert.equal(h.key('KeyC', { altKey: false }).defaultPrevented, false);
  h.controller.dispose();
});

test('AltGraph synthetic Ctrl does not silently open a separate tab', () => {
  const h = harness();
  const altGraph = () => true;
  h.key('AltRight', { key: 'AltGraph', ctrlKey: true, getModifierState: altGraph });
  h.key('KeyL', { ctrlKey: true, getModifierState: altGraph });
  assert.deepEqual(h.hits.nav, ['/leaderboard']);
  assert.deepEqual(h.hits.tabs, []);
  h.controller.dispose();
});

test('real Ctrl + AltRight retains the new-tab behavior, including shifted shortcuts', () => {
  const h = harness();
  h.key('ControlLeft', { ctrlKey: true, altKey: false });
  h.key('AltRight', { ctrlKey: true, key: 'AltGraph', getModifierState: () => true });
  assert.equal(h.doc.documentElement.dataset.tfShortcuts, 'new-tab');
  h.key('KeyC', { ctrlKey: true, getModifierState: () => true });
  assert.deepEqual(h.hits.tabs, ['/courses']);
  h.key('KeyC', { ctrlKey: true }, 'keyup');
  h.key('KeyA', { ctrlKey: true, shiftKey: true, getModifierState: () => true });
  assert.deepEqual(h.hits.tabs, ['/courses', '/admin/analytics']);
  assert.deepEqual(h.hits.nav, []);
  h.controller.dispose();
});
