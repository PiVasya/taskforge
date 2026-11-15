import {
  MIN_SOLVE_EDITOR_HEIGHT,
  MAX_SOLVE_EDITOR_HEIGHT,
  normalizeSolveEditorHeight,
  readSolveEditorHeight,
  writeSolveEditorHeight,
} from './solveEditorHeight';

describe('per-assignment code editor height', () => {
  const storage = () => {
    const rows = new Map();
    return {
      getItem: (key) => rows.has(key) ? rows.get(key) : null,
      setItem: (key, value) => rows.set(key, value),
    };
  };

  test('keeps independent heights across tasks and reloads', () => {
    const local = storage();
    expect(readSolveEditorHeight('task-a', 380, local)).toBe(380);
    expect(readSolveEditorHeight('task-b', 460, local)).toBe(460);
    expect(writeSolveEditorHeight('task-a', 615, local)).toBe(true);
    expect(writeSolveEditorHeight('task-b', 290, local)).toBe(true);
    expect(readSolveEditorHeight('task-a', 380, local)).toBe(615);
    expect(readSolveEditorHeight('task-b', 380, local)).toBe(290);
    expect(readSolveEditorHeight('TASK-A', 460, local)).toBe(615);
  });

  test('clamps malformed or extreme persisted heights', () => {
    const local = storage();
    local.setItem('taskforge:solve-editor-height:v1:test', 'not-a-number');
    expect(readSolveEditorHeight('test', 460, local)).toBe(460);
    local.setItem('taskforge:solve-editor-height:v1:test', '999999');
    expect(readSolveEditorHeight('test', 460, local)).toBe(MAX_SOLVE_EDITOR_HEIGHT);
    local.setItem('taskforge:solve-editor-height:v1:test', '-100');
    expect(readSolveEditorHeight('test', 460, local)).toBe(MIN_SOLVE_EDITOR_HEIGHT);
    expect(normalizeSolveEditorHeight(Infinity, 380)).toBe(380);
  });

  test('does not crash if browser storage is missing or blocked', () => {
    const denied = { getItem: () => { throw new Error('blocked'); }, setItem: () => { throw new Error('blocked'); } };
    expect(readSolveEditorHeight('test', 420, denied)).toBe(420);
    expect(writeSolveEditorHeight('test', 450, denied)).toBe(false);
    expect(writeSolveEditorHeight('', 450, storage())).toBe(false);
  });
});
