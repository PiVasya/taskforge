import {
  getCompilerDiagnostics,
  getPolicyDiagnostics,
  getRunnerError,
  getSolutionFailureCategory,
  getSolutionPassedFailed,
} from './solutionDto';

describe('solutionDto diagnostics', () => {
  test('does not report 0/0 when analyzer stopped execution before tests', () => {
    const result = getSolutionPassedFailed({
      status: 'PolicyFailed',
      passedCount: 0,
      failedCount: 0,
      totalCount: 0,
      result: { policyFailed: true, raw: { policyKind: 'task' } },
    });

    expect(result).toEqual({ passed: null, failed: null, total: 0, testsRan: false });
    expect(getSolutionFailureCategory({ status: 'PolicyFailed', result: { raw: { policyKind: 'task' } } }))
      .toBe('Учебное ограничение');
  });

  test('reads compiler diagnostics from nested raw payload', () => {
    const solution = {
      status: 'CompileError',
      result: {
        raw: {
          status: 'compile_error',
          compileStderr: 'Program.cs(7,15): error CS1002: ; expected',
        },
      },
    };

    expect(getSolutionFailureCategory(solution)).toBe('Компиляция');
    expect(getRunnerError(solution)).toContain('Program.cs(7,15)');
  });

  test('parses compiler line and column and attaches the submitted source fragment', () => {
    const solution = {
      status: 'CompileError',
      code: 'int main() {\n  prinf("x");\n  return 0;\n}',
      result: {
        raw: {
          compileStderr: '[временный файл]:2:3: error: \'prinf\' was not declared in this scope',
        },
      },
    };

    expect(getCompilerDiagnostics(solution)).toEqual([
      expect.objectContaining({
        line: 2,
        column: 3,
        severity: 'error',
        message: expect.stringContaining('prinf'),
        preview: '  prinf("x");',
      }),
    ]);
  });

  test('returns analyzer location and fragment as machine-readable diagnostics', () => {
    const solution = {
      status: 'PolicyFailed',
      result: {
        raw: {
          policyKind: 'task',
          errors: [{
            code: 'forbidden_call',
            pattern_id: 'task.forbidden_call',
            message: 'Запрещено: goto',
            needle: 'goto',
            line: 12,
            column: 5,
            preview: 'goto finish;',
          }],
          hits: [],
        },
      },
    };

    expect(getPolicyDiagnostics(solution)).toEqual([
      expect.objectContaining({
        code: 'forbidden_call',
        patternId: 'task.forbidden_call',
        needle: 'goto',
        line: 12,
        column: 5,
        preview: 'goto finish;',
      }),
    ]);
  });
});
