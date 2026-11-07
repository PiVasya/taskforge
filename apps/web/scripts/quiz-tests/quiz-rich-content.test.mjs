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

test('a fill blank split across marked text nodes remains answerable', () => {
  const rich = JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [
    { type: 'text', text: 'Вставьте __', marks: [{ type: 'bold' }] },
    { type: 'text', text: '_ здесь', marks: [{ type: 'italic' }] },
  ] }] });
  assert.equal(quizHasInlineBlank({ promptContentJson: rich }), true);
  const broken = JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [
    { type: 'text', text: '__' }, { type: 'image', attrs: { src: '/api/files/1' } }, { type: 'text', text: '_' },
  ] }] });
  assert.equal(quizHasInlineBlank({ promptContentJson: broken }), false);
});

test('learner and review rendering remains independent of Tiptap editor runtime', () => {
  const viewSource = readFileSync(new URL('../../src/features/task-test/QuizRichView.jsx', import.meta.url), 'utf8');
  const staticSource = readFileSync(new URL('../../src/components/tiptap/StaticStatementViewer.jsx', import.meta.url), 'utf8');
  assert.match(viewSource, /components\/tiptap\/StaticStatementViewer/);
  assert.doesNotMatch(viewSource, /components\/tiptap\/StatementViewer['"]/);
  assert.doesNotMatch(staticSource, /@tiptap\//);
  assert.match(staticSource, /quizAnswerBlank/);
  assert.match(staticSource, /mathInline/);
});

test('an empty migrated document does not hide the original legacy text', () => {
  const richEmpty = JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [] }] });
  const recovered = quizRichValue(richEmpty, 'Старый вопрос');
  assert.equal(quizDocumentPlainText(recovered), 'Старый вопрос');
  const richContent = JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [
    { type: 'text', text: 'Новый вопрос', marks: [{ type: 'bold' }] },
  ] }] });
  assert.equal(quizRichValue(richContent, 'Старый вопрос'), richContent);
});
