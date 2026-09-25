import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../src/features/course-assignments/courseMapNodeMeta.js', import.meta.url), 'utf8');
const model = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('SQL cards never expose the generic assignment language', () => {
  assert.equal(model.getCourseMapAssignmentFooterLabel('sql-test', { language: 'csharp' }, 'SQL'), '');
  assert.equal(model.getCourseMapAssignmentFooterLabel('sql-test', { allowedLanguages: ['cpp'] }, 'SQL'), '');
});

test('code cards show human programming language names instead of runtime keys', () => {
  assert.equal(model.getCourseMapAssignmentFooterLabel('code-test', { language: 'csharp' }, 'Код'), 'C#');
  assert.equal(model.getCourseMapAssignmentFooterLabel('code-test', { language: 'cpp' }, 'Код'), 'C++');
  assert.equal(model.getCourseMapAssignmentFooterLabel('code-test', { language: 'python3' }, 'Код'), 'Python');
  assert.equal(model.getCourseMapAssignmentFooterLabel('code-test', { allowedLanguages: ['javascript'] }, 'Код'), 'JS');
});

test('image code cards use the same human language labels', () => {
  assert.equal(model.getCourseMapAssignmentFooterLabel('image-test', { language: 'csharp' }, 'Задание'), 'C#');
  assert.equal(model.getCourseMapAssignmentFooterLabel('image-test', { allowedLanguages: ['pascal'] }, 'Задание'), 'Pascal');
});

test('non-code cards ignore accidental generic language fields', () => {
  assert.equal(model.getCourseMapAssignmentFooterLabel('math', { language: 'csharp' }, 'Задание'), 'Задание');
  assert.equal(model.getCourseMapAssignmentFooterLabel('test', { language: 'csharp' }, '5 вопросов'), '5 вопросов');
});


test('code terminal shows the assignment title and only adds solved text after completion', () => {
  assert.deepEqual(
    model.getCourseMapCodeTerminalModel({ title: '2. Двоичный поиск', language: 'javascript' }),
    { title: '2. Двоичный поиск', languageLabel: 'JS', solved: false, statusLabel: '' },
  );

  assert.deepEqual(
    model.getCourseMapCodeTerminalModel({ title: '1. Ввод', language: 'csharp', progressStatus: 'solved' }),
    { title: '1. Ввод', languageLabel: 'C#', solved: true, statusLabel: 'решено' },
  );
});

test('code node shell prompt puts language on the second line and solved output stays lowercase', async () => {
  const nodeSource = await fs.readFile(new URL('../../src/features/course-assignments/nodes/CodeTestNode.jsx', import.meta.url), 'utf8');
  assert.match(nodeSource, />~#<\/span>/);
  assert.doesNotMatch(nodeSource, /course-map-code-terminal-bar/);
  assert.match(nodeSource, /course-map-code-terminal-line is-language/);
  assert.ok(nodeSource.includes('<span className="course-map-code-terminal-language">{terminal.languageLabel}</span>'));
  assert.ok(nodeSource.indexOf('is-language') < nodeSource.indexOf('is-result'));
  assert.equal(model.getCourseMapCodeTerminalModel({ title: 'Probe', isSolved: true }).statusLabel, 'решено');
});
