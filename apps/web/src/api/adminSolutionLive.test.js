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

  addEventListener(name, listener) {
    this.listeners.set(name, listener);
  }

  emit(name, data) {
    this.listeners.get(name)?.({ data });
  }

  close() {
    this.closed = true;
  }
}

describe('admin solution SSE client', () => {
  beforeEach(() => {
    FakeEventSource.instances = [];
    global.EventSource = FakeEventSource;
  });

  afterEach(() => {
    delete global.EventSource;
  });

  test('opens the same-origin SSE endpoint with credentials and emits parsed solution events', () => {
    const onEvent = jest.fn();
    const onOpen = jest.fn();
    const onError = jest.fn();

    const dispose = subscribeAdminSolutionEvents({ onEvent, onOpen, onError });
    const source = FakeEventSource.instances[0];

    expect(source.url).toBe('/api/admin/solution-events');
    expect(source.options).toEqual({ withCredentials: true });

    source.onopen();
    expect(onOpen).toHaveBeenCalledTimes(1);

    source.emit('solution', JSON.stringify({ itemId: 'solution-1', userId: 'user-1' }));
    expect(onEvent).toHaveBeenCalledWith({ itemId: 'solution-1', userId: 'user-1' });

    source.emit('solution', '{invalid json');
    expect(onEvent).toHaveBeenCalledTimes(1);

    source.onerror(new Error('network'));
    expect(onError).toHaveBeenCalledWith(expect.any(Error), 1);

    dispose();
    expect(source.closed).toBe(true);
  });
});
