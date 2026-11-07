import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../src/features/task-test/quizRichContent.js', import.meta.url), 'utf8');
const { literalTextDocument, parseQuizDocument, quizRichValue, quizDocumentPlainText, normalizeQuizQuestions, setQuizQuestionEditorMode, hasQuizContent, quizHasInlineBlank } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('legacy TXT questions become literal Tiptap documents with blanks intact', () => {
  const old = 'Вставьте ___ в x < 7\nВторая строка `без Markdown`';
  const rich = quizRichValue('', old);
  const doc = parseQuizDocument(rich);
  assert.equal(doc.content[0].content[0].text, 'Вставьте ___ в x < 7');
  assert.equal(doc.content[1].content[0].text, 'Вторая строка `без Markdown`');
  assert.equal(quizDocumentPlainText(rich), old);
  assert.equal(quizHasInlineBlank({ promptContentJson: rich }), true);
});

test('one question mode controls all answers without altering their rich documents', () => {
  const rich = JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [
    { type: 'text', text: '7', marks: [{ type: 'superscript' }] },
  ] }] });
  const [q] = normalizeQuizQuestions([{ prompt: 'Сколько?', options: [
    { key: 'a', text: '7' }, { key: 'b', text: '7', contentJson: rich, editorMode: 'rich' },
  ] }]);
  assert.equal(q.promptEditorMode, 'plain');
  assert.equal(q.options[0].editorMode, 'plain');
  assert.equal(q.options[1].editorMode, 'plain'); // Legacy per-option mode is ignored.
  assert.equal(q.options[1].contentJson, rich);
  assert.equal(q.optionsLayout, 'list');
  const enabled = setQuizQuestionEditorMode(q, 'rich');
  assert.equal(enabled.promptEditorMode, 'rich');
  assert.ok(enabled.options.every((option) => option.editorMode === 'rich'));
  assert.equal(enabled.options[1].contentJson, rich);
  const disabled = setQuizQuestionEditorMode(enabled, 'plain');
  assert.equal(disabled.promptEditorMode, 'plain');
  assert.ok(disabled.options.every((option) => option.editorMode === 'plain'));
  assert.equal(disabled.options[1].contentJson, rich);
  assert.equal(q.promptEditorMode, 'plain'); // No accidental mutation of the original.
  assert.equal(q.options[0].contentJson, quizRichValue('', '7'));

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
  assert.equal(hasQuizContent(richEmpty, 'Старый вопрос'), true);
  assert.equal(hasQuizContent(richEmpty, ''), false);
  const richContent = JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [
    { type: 'text', text: 'Новый вопрос', marks: [{ type: 'bold' }] },
  ] }] });
  assert.equal(quizRichValue(richContent, 'Старый вопрос'), richContent);
});

test('quiz editor exposes one tiny theme-neutral mode toggle per question, with no choice toggles or visible hints', () => {
  const editor = readFileSync(new URL('../../src/features/task-test/TaskTestEditor.jsx', import.meta.url), 'utf8');
  const css = readFileSync(new URL('../../src/features/task-test/task-test-editor.css', import.meta.url), 'utf8');
  assert.match(editor, /toggleQuestionFormatting/);
  assert.match(editor, /setQuizQuestionEditorMode/);
  assert.match(editor, /aria-pressed=\{q\.promptEditorMode === 'rich'\}/);
  assert.doesNotMatch(editor, /ChevronUp|Свернуть редактор вопроса|Свернуть вариант/);
  assert.equal((editor.match(/<PenLine\b/g) || []).length, 1);
  assert.match(editor, /mode=\{mode\}/); // Options inherit the parent's mode.
  assert.match(editor, /const mode = q\.promptEditorMode === 'rich'/);
  assert.doesNotMatch(editor, /renderModeButtons|>TXT<|>Tiptap<|>Редактировать<|Нажми, чтобы/);
  assert.match(css, /width: 22px;/);
  assert.match(css, /box-shadow: none;/);
  assert.doesNotMatch(css, /brand-300|btn-primary/);
});

test('authoring surfaces contain no instructional help lines or search decorations', () => {
  const search = readFileSync(new URL('../../src/features/admin-solutions/components/AdminUserSearchCombobox.jsx', import.meta.url), 'utf8');
  const tiptap = readFileSync(new URL('../../src/components/tiptap/StatementEditor.jsx', import.meta.url), 'utf8');
  assert.doesNotMatch(search, /<Search|\bSearch, X\b|Поиск учитывает|Аккаунт выбирается|Совпадения ранжируются|searchMatchLabel/);
  assert.doesNotMatch(tiptap, /Можно вставлять картинки/);
  assert.doesNotMatch(tiptap, /<ToolbarButton[^>]*title=\{/);
});

test('Tiptap highlight color picker uses compact theme-aware swatches instead of a wide native select', () => {
  const editor = readFileSync(new URL('../../src/components/tiptap/StatementEditor.jsx', import.meta.url), 'utf8');
  const styles = readFileSync(new URL('../../src/components/tiptap/tiptap.css', import.meta.url), 'utf8');
  assert.match(editor, /HIGHLIGHT_COLORS\.map/);
  assert.match(editor, /aria-pressed=\{highlightColor === color\}/);
  assert.match(editor, /setHighlight\(\{ color \}\)/);
  assert.match(styles, /\.tiptap-highlight-swatches/);
  assert.match(styles, /background: rgb\(var\(--card\)\)/);
  assert.doesNotMatch(editor, /<option value="#bbf7d0"/);
});
