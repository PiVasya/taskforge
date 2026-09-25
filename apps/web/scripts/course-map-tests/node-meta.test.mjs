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
