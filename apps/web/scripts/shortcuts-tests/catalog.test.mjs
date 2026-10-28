import test from 'node:test';
import assert from 'node:assert/strict';
import { loadSourceModule } from '../test-utils/load-source-module.mjs';
const { SHORTCUTS, navigationShortcuts, shortcutKeys, shortcutForHref, shortcutAllowed } = await loadSourceModule(new URL('../../src/features/shortcuts/shortcutCatalog.js', import.meta.url));
const { safeShortcutHref, eventKeyCode } = await loadSourceModule(new URL('../../src/features/shortcuts/shortcutController.js', import.meta.url));
const learner = { authenticated: true, admin: false, editor: false, minecraft: false };

test('navigation chords are unique for a fully authorized user', () => {
  const rows = navigationShortcuts({ authenticated: true, admin: true, editor: true, minecraft: true });
  assert.equal(new Set(rows.map(row => `${row.shift ? 'shift:' : ''}${row.code}`)).size, rows.length);
  assert.ok(rows.length >= 18);
});
test('anonymous and learner catalogs do not expose administrative navigation', () => {
  assert.deepEqual(navigationShortcuts({ authenticated: false }).map(x => x.id), ['nav.news', 'nav.courses']);
  assert.equal(navigationShortcuts(learner).some(x => x.role === 'admin'), false);
  assert.equal(navigationShortcuts(learner).some(x => x.id === 'nav.minecraft'), false);
});
test('support uses the real admin route without granting access by URL guessing', () => {
  assert.equal(navigationShortcuts({ ...learner, admin: true }).find(x => x.id === 'nav.support').href, '/admin/support');
  assert.equal(shortcutAllowed(SHORTCUTS.merge, learner), false);
  assert.equal(shortcutAllowed(SHORTCUTS.merge, { ...learner, admin: true }), true);
});
test('new tab labels add Ctrl but retain each original chord', () => {
  assert.deepEqual(shortcutKeys('nav.courses'), ['Alt', 'C']);
  assert.deepEqual(shortcutKeys('nav.courses', true), ['Ctrl', 'Alt', 'C']);
  assert.deepEqual(shortcutKeys('nav.users', true), ['Ctrl', 'Alt', 'Shift', 'U']);
  assert.deepEqual(shortcutKeys('primary'), ['Alt', 'Enter']);
});
test('route annotation does not guess from substrings or label text', () => {
  assert.equal(shortcutForHref('/courses'), 'nav.courses');
  assert.equal(shortcutForHref('/admin/support'), 'nav.support');
  assert.equal(shortcutForHref('/settings?section=profile'), 'nav.profile');
  assert.equal(shortcutForHref('/courses/secret/edit'), undefined);
  assert.equal(shortcutForHref('https://evil.test/courses'), undefined);
});
test('physical codes work with Cyrillic and numeric keypad Enter', () => {
  assert.equal(eventKeyCode({ code: 'KeyC', key: '\u0441' }), 'KeyC');
  assert.equal(eventKeyCode({ code: 'NumpadEnter', key: 'Enter' }), 'Enter');
  assert.equal(eventKeyCode({ key: 'c' }), 'KeyC');
  assert.equal(eventKeyCode({ key: '8' }), 'Digit8');
});
test('new tabs allow only HTTP same-origin navigation, preserving query/hash', () => {
  const origin = 'https://taskforge.test';
  assert.equal(safeShortcutHref('/leaderboard?days=7#me', origin), '/leaderboard?days=7#me');
  assert.equal(safeShortcutHref(`${origin}/courses`, origin), '/courses');
  for (const href of ['javascript:alert(1)', 'data:text/html,test', '//evil.test/', 'https://evil.test/', 'file:///tmp/code', null]) {
    assert.equal(safeShortcutHref(href, origin), null);
  }
});
