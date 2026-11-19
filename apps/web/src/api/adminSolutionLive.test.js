import { subscribeAdminSolutionEvents } from './adminSolutionEvents';

class FakeEventSource {
  static instances = [];
  constructor(url, options) {
    this.url = url;
    this.options = options;
    this.readyState = 1;
    this.listeners = new Map();
    this.closed = false;
    FakeEventSource.instances.push(this);
  }
  addEventListener(name, listener) { this.listeners.set(name, listener); }
  emit(name, data) { this.listeners.get(name)?.({ data }); }
  close() { this.closed = true; }
}

describe('YugabyteDB-backed administrative SSE feeds', () => {
  beforeEach(() => {
    FakeEventSource.instances = [];
    global.EventSource = FakeEventSource;
  });
  afterEach(() => { delete global.EventSource; });

  test('opens independent owners for code/SQL/images and test/math, with cookies', () => {
    const onEvent = jest.fn();
    const onOpen = jest.fn();
    const onError = jest.fn();
    const dispose = subscribeAdminSolutionEvents({ onEvent, onOpen, onError });
    expect(FakeEventSource.instances.map((x) => x.url)).toEqual([
      '/api/admin/solution-events', '/api/admin/task-solution-events',
    ]);
    for (const source of FakeEventSource.instances) {
      expect(source.options).toEqual({ withCredentials: true });
    }
    FakeEventSource.instances[0].onopen();
    expect(onOpen).not.toHaveBeenCalled();
    FakeEventSource.instances[1].onopen();
    expect(onOpen).toHaveBeenCalledTimes(1);

    FakeEventSource.instances[0].emit('solution', JSON.stringify({ kind: 'code', itemId: 'c' }));
    FakeEventSource.instances[1].emit('solution', JSON.stringify({ kind: 'math', itemId: 'm' }));
    expect(onEvent.mock.calls.map(([e]) => e.kind)).toEqual(['code', 'math']);
    FakeEventSource.instances[0].emit('solution', '{invalid json');
    expect(onEvent).toHaveBeenCalledTimes(2);

    FakeEventSource.instances[1].onerror(new Error('BY / task feed lost'));
    expect(onError).toHaveBeenCalledTimes(1);
    FakeEventSource.instances[1].onopen();
    expect(onOpen).toHaveBeenCalledTimes(2);
    dispose();
    expect(FakeEventSource.instances.every((x) => x.closed)).toBe(true);
    FakeEventSource.instances[0].emit('solution', JSON.stringify({ kind: 'sql' }));
    expect(onEvent).toHaveBeenCalledTimes(2);
  });
});
