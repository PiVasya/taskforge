import { clearPrivateBrowserState } from './privateBrowserState';
import { getSolveDraftStore } from '../features/assignment-solve/solveDraftStore';

describe('private browser state', () => {
  beforeEach(() => {
    jest.useFakeTimers();
    window.localStorage.clear();
  });

  afterEach(() => {
    jest.runOnlyPendingTimers();
    jest.useRealTimers();
    window.localStorage.clear();
  });

  test('logout cleanup removes learner solution data but preserves UI preferences', () => {
    const assignmentId = 'assignment-a';
    const store = getSolveDraftStore(assignmentId);
    store.initialize({ code: 'secret code', language: 'cpp' });

    window.localStorage.setItem('results:assignment-a', '{"secret":true}');
    window.localStorage.setItem('image-results:assignment-a', '{"secret":true}');
    window.localStorage.setItem('taskforge-sql:user-a:assignment-a:profile-a', 'SELECT secret');
    window.localStorage.setItem('taskforge.compiler.draft.v1.cpp', 'secret compiler draft');
    window.localStorage.setItem('taskforge.theme', 'neo-brutal');
    window.localStorage.setItem('taskforge.colorTheme', 'purple');

    clearPrivateBrowserState();

    expect(window.localStorage.getItem('solve-draft:v3:assignment-a')).toBeNull();
    expect(window.localStorage.getItem('results:assignment-a')).toBeNull();
    expect(window.localStorage.getItem('image-results:assignment-a')).toBeNull();
    expect(window.localStorage.getItem('taskforge-sql:user-a:assignment-a:profile-a')).toBeNull();
    expect(window.localStorage.getItem('taskforge.compiler.draft.v1.cpp')).toBeNull();
    expect(window.localStorage.getItem('taskforge.theme')).toBe('neo-brutal');
    expect(window.localStorage.getItem('taskforge.colorTheme')).toBe('purple');
  });

  test('a stale in-memory draft store cannot resurrect code after cleanup', () => {
    const assignmentId = 'assignment-b';
    const staleStore = getSolveDraftStore(assignmentId);
    staleStore.initialize({ code: 'before logout', language: 'cpp' });
    staleStore.setCode('queued autosave');

    clearPrivateBrowserState();

    staleStore.setCode('must never return');
    jest.advanceTimersByTime(1000);

    expect(window.localStorage.getItem('solve-draft:v3:assignment-b')).toBeNull();
  });
});
