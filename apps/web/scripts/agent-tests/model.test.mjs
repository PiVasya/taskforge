import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../src/features/agent/agentModel.js', import.meta.url), 'utf8');
const model = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('AI conversation title stays compact for a long first prompt', () => {
  const input = `  Создай курс   ${'SQL '.repeat(200)}  `;
  const title = model.buildConversationTitle(input);
  assert.ok(Array.from(title).length <= 160);
  assert.ok(title.endsWith('…'));
  assert.equal(/\s{2,}/.test(title), false);
});

test('AI conversation title has a stable fallback and preserves short text', () => {
  assert.equal(model.buildConversationTitle('   '), 'AI-чат');
  assert.equal(model.buildConversationTitle('Короткий запрос'), 'Короткий запрос');
});
