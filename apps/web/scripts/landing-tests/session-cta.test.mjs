import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../src/features/landing/landingSessionModel.js', import.meta.url), 'utf8');
const model = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('guest CTA points to registration and login', () => {
  const cta = model.getLandingSessionCta({ ready: true, access: null });
  assert.equal(cta.pending, false);
  assert.equal(cta.authenticated, false);
  assert.deepEqual(cta.primary, { href: '/register', text: 'Начать обучение' });
  assert.deepEqual(cta.secondary, { href: '/login', text: 'Войти' });
  assert.match(cta.final.text, /Создай аккаунт/);
});

test('authenticated CTA makes courses the primary action and removes registration copy', () => {
  const cta = model.getLandingSessionCta({ ready: true, access: 'token' });
  assert.equal(cta.pending, false);
  assert.equal(cta.authenticated, true);
  assert.deepEqual(cta.primary, { href: '/courses', text: 'Открыть курсы' });
  assert.deepEqual(cta.secondary, { href: '/news', text: 'Новости' });
  assert.equal(cta.final.title, 'Продолжить обучение?');
  assert.doesNotMatch(cta.final.text, /Создай аккаунт/);
});

test('unknown session state exposes no guest or authenticated navigation target', () => {
  const cta = model.getLandingSessionCta({ ready: false, access: null });
  assert.equal(cta.pending, true);
  assert.equal(cta.authenticated, false);
  assert.equal(cta.primary, null);
  assert.equal(cta.secondary, null);
  assert.equal(cta.final.title, 'Проверяем вашу сессию');
});
