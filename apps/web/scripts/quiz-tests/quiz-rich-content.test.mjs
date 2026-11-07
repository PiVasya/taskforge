import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../src/features/task-test/quizRichContent.js', import.meta.url), 'utf8');
const { literalTextDocument, parseQuizDocument, quizRichValue, quizDocumentPlainText, normalizeQuizQuestions, hasQuizContent, quizHasInlineBlank } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('legacy TXT questions become literal Tiptap documents with blanks intact', () => {
  const old = 'Вставьте ___ в x < 7\nВторая строка `без Markdown`';
  const rich = quizRichValue('', old);
  const doc = parseQuizDocument(rich);
  assert.equal(doc.content[0].content[0].text, 'Вставьте ___ в x < 7');
  assert.equal(doc.content[1].content[0].text, 'Вторая строка `без Markdown`');
  assert.equal(quizDocumentPlainText(rich), old);
  assert.equal(quizHasInlineBlank({ promptContentJson: rich }), true);
});

test('normalization keeps independently selected TXT/Tiptap modes and rich choices', () => {
  const rich = JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [
    { type: 'text', text: '7', marks: [{ type: 'superscript' }] },
  ] }] });
  const [q] = normalizeQuizQuestions([{ prompt: 'Сколько?', options: [
    { key: 'a', text: '7' }, { key: 'b', text: '7', contentJson: rich, editorMode: 'rich' },
  ] }]);
  assert.equal(q.promptEditorMode, 'plain');
  assert.equal(q.options[0].editorMode, 'plain');
  assert.equal(q.options[1].editorMode, 'rich');
  assert.equal(q.options[1].contentJson, rich);
  assert.equal(q.optionsLayout, 'list');
});

test('images and native MathML nodes count as question/choice content', () => {
  const rich = JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [
    { type: 'mathInline', attrs: { kind: 'fraction', numerator: '1', denominator: '7' } },
  ] }] });
  assert.equal(hasQuizContent(rich), true);
  assert.equal(quizDocumentPlainText(rich), '(1)/(7)');
  assert.equal(hasQuizContent(JSON.stringify({ type: 'doc', content: [{ type: 'image', attrs: { src: '/api/files/1' } }] })), true);
  assert.equal(hasQuizContent(JSON.stringify(literalTextDocument(''))), false);
});
