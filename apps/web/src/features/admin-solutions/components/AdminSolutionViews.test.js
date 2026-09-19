import React from 'react';
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { RunnerOutput } from './AdminSolutionViews';

describe('admin RunnerOutput', () => {
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

  test('shows every executed case including hidden inputs and expected output', async () => {
    const cases = Array.from({ length: 10 }, (_, index) => ({
      input: index === 9 ? 'SECRET_HIDDEN_INPUT' : `input-${index + 1}`,
      expectedOutput: index === 9 ? 'SECRET_EXPECTED_OUTPUT' : `expected-${index + 1}`,
      actualOutput: index === 9 ? 'SECRET_EXPECTED_OUTPUT' : `expected-${index + 1}`,
      passed: true,
      status: 'ok',
      hidden: index >= 6,
    }));

    await act(async () => {
      root.render(<RunnerOutput item={{ result: { cases } }} />);
    });

    expect(container.querySelectorAll('[data-testid="admin-runner-test-case"]')).toHaveLength(10);
    expect(container.querySelectorAll('[data-hidden-test="true"]')).toHaveLength(4);
    expect(container.textContent).toContain('Всего: 10');
    expect(container.textContent).toContain('Скрытых: 4');
    expect(container.textContent).toContain('SECRET_HIDDEN_INPUT');
    expect(container.textContent).toContain('SECRET_EXPECTED_OUTPUT');
    expect(container.textContent).toContain('Тест #10');
    expect(container.textContent).not.toContain('… и ещё');
  });
});
