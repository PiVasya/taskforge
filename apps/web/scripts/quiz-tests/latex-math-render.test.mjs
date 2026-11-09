import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const original = readFileSync(new URL('../../src/components/tiptap/latexMath.js', import.meta.url), 'utf8');
const calls = [];
const katex = {
  renderToString(latex, options) {
    calls.push({ latex, options });
    if (latex === '\\badcommand') throw new Error('ParseError');
    return `<math>${latex}</math>`;
  },
};
const injected = original
  .replace("import katex from 'katex';", 'const katex = globalThis.__mockKatexForQuizTest;')
  .replace("import 'katex/dist/katex.min.css';", '');
globalThis.__mockKatexForQuizTest = katex;
const { renderLatexMathHtml, MAX_LATEX_LENGTH } = await import(`data:text/javascript;base64,${Buffer.from(injected).toString('base64')}`);
delete globalThis.__mockKatexForQuizTest;

test('LaTeX renders inline and display math with browser-safe KaTeX settings', () => {
  assert.equal(renderLatexMathHtml('\\frac{1}{7}'), '<math>\\frac{1}{7}</math>');
  assert.equal(renderLatexMathHtml('\\int_0^1 x^2\\,dx', true), '<math>\\int_0^1 x^2\\,dx</math>');
  assert.equal(calls.length, 2);
  assert.equal(calls[0].options.displayMode, false);
  assert.equal(calls[1].options.displayMode, true);
  for (const { options } of calls) {
    assert.equal(options.output, 'htmlAndMathml');
    assert.equal(options.trust, false);
    assert.equal(options.throwOnError, true);
    assert.ok(options.maxExpand > 0 && options.maxExpand <= 1000);
    assert.ok(options.maxSize > 0 && options.maxSize <= 30);
  }
});

test('LaTeX rejects invalid, empty, oversized, and non-string documents without calling KaTeX', () => {
  assert.equal(renderLatexMathHtml('\\badcommand'), null);
  const before = calls.length;
  for (const input of ['', '   ', null, 42, [], 'x'.repeat(MAX_LATEX_LENGTH + 1)]) {
    assert.equal(renderLatexMathHtml(input), null);
  }
  assert.equal(calls.length, before);
});
