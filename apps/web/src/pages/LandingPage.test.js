import React from 'react';
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { MemoryRouter } from 'react-router-dom';
import LandingPage from './LandingPage';
import { useAuth } from '../auth/AuthContext';

jest.mock('../auth/AuthContext', () => ({
  useAuth: jest.fn(),
}));

describe('LandingPage session-aware calls to action', () => {
  let container;
  let root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    useAuth.mockReset();
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  async function renderWithAuth(value) {
    useAuth.mockReturnValue(value);
    await act(async () => {
      root.render(
        <MemoryRouter>
          <LandingPage />
        </MemoryRouter>,
      );
    });
  }

  function linksWithText(text) {
    return [...container.querySelectorAll('a')].filter((link) => link.textContent.includes(text));
  }

  test('guest sees registration and login actions', async () => {
    await renderWithAuth({ ready: true, access: null });

    const registerLinks = linksWithText('Начать обучение');
    expect(registerLinks).toHaveLength(2);
    expect(registerLinks.every((link) => link.getAttribute('href') === '/register')).toBe(true);

    const loginLink = linksWithText('Войти')[0];
    expect(loginLink?.getAttribute('href')).toBe('/login');
    expect(container.textContent).toContain('Создай аккаунт, выбери курс и переходи к первому заданию.');
  });

  test('authenticated user gets courses as the primary action on both calls to action', async () => {
    await renderWithAuth({ ready: true, access: 'access-token' });

    const courseLinks = linksWithText('Открыть курсы');
    expect(courseLinks).toHaveLength(2);
    expect(courseLinks.every((link) => link.getAttribute('href') === '/courses')).toBe(true);

    const newsLink = linksWithText('Новости')[0];
    expect(newsLink?.getAttribute('href')).toBe('/news');
    expect(container.textContent).toContain('Продолжить обучение?');
    expect(container.textContent).toContain('Открой курсы, выбери доступный маршрут и переходи к следующему заданию.');
    expect(container.textContent).not.toContain('Создай аккаунт, выбери курс и переходи к первому заданию.');
  });

  test('landing keeps one process explanation instead of repeating the same journey', async () => {
    await renderWithAuth({ ready: true, access: null });

    expect(container.textContent).toContain('От курса до принятого решения');
    expect(container.textContent).not.toContain('Один понятный путь вместо лишнего шума');
    expect(container.textContent).not.toContain('Открыл курс');
    expect(container.textContent).not.toContain('Решил задачу');
    expect(container.textContent).not.toContain('Получил результат');
  });

  test('solution check is explicitly a demonstration and does not promise a real submit', async () => {
    await renderWithAuth({ ready: true, access: null });

    expect(container.textContent).toContain('Повторить демонстрацию');
    expect(container.textContent).toContain('Это демонстрация интерфейса: код не отправляется на сервер.');
    expect(container.textContent).not.toContain('Отправить решение');
  });

  test('session check exposes no guest navigation before authentication state is known', async () => {
    await renderWithAuth({ ready: false, access: null });

    expect(container.querySelector('a[href="/register"]')).toBeNull();
    expect(container.querySelector('a[href="/login"]')).toBeNull();
    expect(container.querySelector('a[href="/courses"]')).toBeNull();
    expect(container.querySelector('a[href="/news"]')).toBeNull();

    const pendingButtons = [...container.querySelectorAll('button:disabled')]
      .filter((button) => button.textContent.includes('Проверяем сессию'));
    expect(pendingButtons).toHaveLength(2);
    expect(container.textContent).toContain('Проверяем вашу сессию');
  });
});
