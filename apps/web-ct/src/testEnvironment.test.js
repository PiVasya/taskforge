import React, { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router-dom';

test('CT React and Router render and unmount without test-environment warnings', async () => {
  expect(window.IS_REACT_ACT_ENVIRONMENT).toBe(true);

  const errors = [];
  const warnings = [];
  const errorSpy = jest.spyOn(console, 'error').mockImplementation((...args) => errors.push(args));
  const warnSpy = jest.spyOn(console, 'warn').mockImplementation((...args) => warnings.push(args));
  const container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);

  try {
    await act(async () => {
      root.render(
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <span data-testid="ct-router-ready">Готово</span>
        </MemoryRouter>,
      );
    });
    expect(container.querySelector('[data-testid="ct-router-ready"]')?.textContent).toBe('Готово');

    await act(async () => {
      root.unmount();
    });
  } finally {
    container.remove();
    errorSpy.mockRestore();
    warnSpy.mockRestore();
  }

  expect(errors).toEqual([]);
  expect(warnings).toEqual([]);
});
