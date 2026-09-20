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
  test('shows structured compiler diagnostics with source line for admin analysis', async () => {
    await act(async () => {
      root.render(
        <RunnerOutput
          item={{
            status: 'CompileError',
            code: 'int main() {\n  prinf("x");\n}',
            result: {
              raw: {
                compileStderr: '[временный файл]:2:3: error: \'prinf\' was not declared in this scope',
              },
            },
          }}
        />
      );
    });

    expect(container.textContent).toContain('Причина: Компиляция');
    expect(container.textContent).toContain('Диагностика компилятора');
    expect(container.textContent).toContain('Строка: 2');
    expect(container.textContent).toContain('Символ: 3');
    expect(container.textContent).toContain('prinf("x");');
    expect(container.querySelector('[data-failure-category="Компиляция"]')).not.toBeNull();
    expect(container.querySelector('[data-tests-ran="false"]')).not.toBeNull();
  });

  test('separates analyzer rejection from test results and shows exact safe location', async () => {
    await act(async () => {
      root.render(
        <RunnerOutput
          item={{
            status: 'PolicyFailed',
            passedCount: 0,
            failedCount: 0,
            totalCount: 0,
            result: {
              raw: {
                policyKind: 'task',
                errors: [{
                  code: 'forbidden_call',
                  pattern_id: 'task.forbidden_call',
                  message: 'Запрещено: goto',
                  needle: 'goto',
                  line: 4,
                  column: 3,
                  preview: 'goto finish;',
                }],
                hits: [],
              },
            },
          }}
        />
      );
    });

    expect(container.textContent).toContain('Причина: Учебное ограничение');
    expect(container.textContent).toContain('Тесты не запускались');
    expect(container.textContent).toContain('Строка: 4');
    expect(container.textContent).toContain('Символ: 3');
    expect(container.textContent).toContain('goto finish;');
    expect(container.textContent).not.toContain('Пройдено: 0 / Провалено: 0');
  });

});
