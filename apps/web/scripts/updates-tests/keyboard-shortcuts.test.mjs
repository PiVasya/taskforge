import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { loadSourceModule } from '../test-utils/load-source-module.mjs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../');
const read = (name) => JSON.parse(readFileSync(path.join(root, 'public/updates/', name), 'utf8'));
const { SHORTCUTS, shortcutLabel } = await loadSourceModule(new URL('../../src/features/shortcuts/shortcutCatalog.js', import.meta.url));

test('hotkey news is discoverable with a real and unique story', () => {
  const index = read('index.json');
  const matching = index.filter(item => item.id === 'keyboard-shortcuts');
  assert.equal(matching.length, 1);
  assert.equal(matching[0].file, 'keyboard-shortcuts.json');
  const post = read(matching[0].file);
  assert.equal(post.layout, 'showcase');
  assert.ok(post.sections.length >= 3);
});
test('news documents real shortcuts but does not advertise conflicting legacy chords', () => {
  const post = JSON.stringify(read('keyboard-shortcuts.json'));
  assert.equal(shortcutLabel('primary'), 'Alt+Enter');
  assert.equal(shortcutLabel('editorMode'), 'Alt+E');
  assert.equal(shortcutLabel('nav.courses'), 'Alt+C');
  assert.equal(shortcutLabel('nav.solutions'), 'Alt+R');
  assert.equal(SHORTCUTS.sidebar, undefined);
  assert.equal(SHORTCUTS.next, undefined);
  for (const label of ['Alt + Enter', 'Alt + E', 'Alt + C', 'Alt + R']) assert.ok(post.includes(label));
});
