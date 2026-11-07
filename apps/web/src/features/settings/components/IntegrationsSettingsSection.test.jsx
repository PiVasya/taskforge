import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import IntegrationsSettingsSection from './IntegrationsSettingsSection';

const action = () => {};

function telegram(status = { linked: false, botUsername: '@taskforge_bot' }) {
  return {
    status,
    code: null,
    expires: null,
    loading: false,
    error: null,
    refresh: action,
    generate: action,
    copy: action,
    unlink: action,
  };
}

function messengers(status = { telegramLinked: false, primaryMessenger: null }) {
  return { status, loading: false, error: null, chooseTelegram: action };
}

function minecraft() {
  return {
    status: { linked: false, linkCount: 0, links: [] },
    nick: '',
    setNick: action,
    inputCode: '',
    setInputCode: action,
    expires: null,
    delivery: null,
    loading: false,
    error: null,
    refresh: action,
    request: action,
    confirm: action,
    unlink: action,
  };
}

test('integrations settings preserve Telegram and Minecraft linking without showing the unavailable messenger', () => {
  const html = renderToStaticMarkup(
    <IntegrationsSettingsSection telegram={telegram()} messengers={messengers()} minecraft={minecraft()} />,
  );

  expect(html).toContain('Telegram');
  expect(html).toContain('Сгенерировать код');
  expect(html).toContain('Minecraft');
  expect(html).toContain('settings-minecraft-nick');
  expect(html).not.toContain('Discord');
  expect(html).not.toContain('Основной мессенджер');
  expect(html).not.toContain('primary-messenger');
});

test('linked Telegram account remains manageable while the unavailable messenger is hidden', () => {
  const html = renderToStaticMarkup(
    <IntegrationsSettingsSection telegram={telegram({ linked: true, username: '@learner' })} messengers={messengers({ telegramLinked: true, primaryMessenger: 'telegram' })} minecraft={minecraft()} />,
  );

  expect(html).toContain('@learner');
  expect(html).toContain('Удалить привязку');
  expect(html).toContain('Minecraft');
  expect(html).not.toContain('Discord');
});


test('an account with a previously selected unavailable delivery channel can switch support replies to Telegram', () => {
  const html = renderToStaticMarkup(
    <IntegrationsSettingsSection
      telegram={telegram({ linked: true, username: '@learner' })}
      messengers={messengers({ telegramLinked: true, primaryMessenger: 'discord' })}
      minecraft={minecraft()}
    />,
  );

  expect(html).toContain('Сделать Telegram основным');
  expect(html).toContain('Ответы техподдержки');
  expect(html).not.toContain('Discord');
  expect(html).not.toContain('Discord-бот');
});
