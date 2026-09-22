import React from 'react';
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import PublicProfileCard from './PublicProfileCard';

describe('PublicProfileCard statistics', () => {
  let container;
  let root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  test('renders real aggregate and per-kind activity instead of fixed zeroes', async () => {
    await act(async () => {
      root.render(<PublicProfileCard profile={{
        displayName: 'User',
        statsVisible: true,
        statsReliable: true,
        score: 145,
        solvedAssignments: 12,
        totalAttempts: 37,
        codeSolutions: 14,
        sqlSolutions: 6,
        imageSolutions: 3,
        testAttempts: 10,
        mathAttempts: 4,
        linksVisible: false,
        bioVisible: false,
      }} />);
    });

    expect(container.textContent).toContain('145 рейтинга');
    expect(container.textContent).toContain('12 решённых заданий');
    expect(container.textContent).toContain('37 попыток отправки решений');
    expect(container.textContent).toContain('Код: 14');
    expect(container.textContent).toContain('SQL: 6');
    expect(container.textContent).toContain('Тесты: 10');
  });

  test('does not lie with zeroes when activity backends are unavailable', async () => {
    await act(async () => {
      root.render(<PublicProfileCard profile={{
        displayName: 'User',
        statsVisible: true,
        statsReliable: false,
        linksVisible: false,
        bioVisible: false,
      }} />);
    });

    expect(container.textContent).toContain('Статистика временно недоступна');
    expect(container.textContent).not.toContain('0 решённых заданий');
  });

  test('honors the public stats visibility flag', async () => {
    await act(async () => {
      root.render(<PublicProfileCard profile={{
        displayName: 'User',
        statsVisible: false,
        linksVisible: false,
        bioVisible: false,
      }} />);
    });

    expect(container.textContent).not.toContain('Статистика');
    expect(container.textContent).not.toContain('решённых заданий');
  });
});
