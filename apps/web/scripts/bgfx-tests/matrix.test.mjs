import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const source = await readFile(new URL('../../src/components/bgfx/matrixPalette.js', import.meta.url), 'utf8');
const { makeMatrixThemePalette } = await import(`data:text/javascript,${encodeURIComponent(source)}`);

const themes = {
  blue: { accent: [14, 165, 233], foreground: [203, 213, 225] },
  pink: { accent: [236, 72, 153], foreground: [253, 224, 239] },
  apple: { accent: [16, 185, 129], foreground: [209, 250, 229] },
  red: { accent: [239, 68, 68], foreground: [254, 226, 226] },
  honey: { accent: [245, 158, 11], foreground: [254, 243, 199] },
  violet: { accent: [168, 85, 247], foreground: [237, 233, 254] },
};

for (const [name, { accent, foreground }] of Object.entries(themes)) {
  test(`Matrix uses the ${name} palette for every dark-theme trail symbol`, () => {
    const palette = makeMatrixThemePalette(true, foreground, accent);
    assert.deepEqual(palette.head, foreground);
    assert.deepEqual(palette.trail, accent);
  });

  test(`Matrix retains the ${name} light-theme colors`, () => {
    const palette = makeMatrixThemePalette(false, foreground, accent);
    assert.deepEqual(palette.head, accent);
    assert.deepEqual(palette.trail, accent);
  });
}

test('Matrix colors follow a theme switch rather than keeping a fixed green tint', () => {
  const pink = makeMatrixThemePalette(true, themes.pink.foreground, themes.pink.accent);
  const violet = makeMatrixThemePalette(true, themes.violet.foreground, themes.violet.accent);
  assert.notDeepEqual(pink.trail, violet.trail);
  assert.notDeepEqual(pink.head, violet.head);
});
