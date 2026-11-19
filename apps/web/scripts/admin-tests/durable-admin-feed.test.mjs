import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(here, '../../src/api/adminSolutionEvents.js'), 'utf8');

function setup() {
  const sources = [];
  class FakeEventSource {
    constructor(url, options) {
      this.url = url;
      this.options = options;
      this.readyState = 1;
      this.listeners = new Map();
      this.closed = false;
      sources.push(this);
    }
    addEventListener(name, listener) { this.listeners.set(name, listener); }
    emit(name, data) { this.listeners.get(name)?.({ data }); }
    close() { this.closed = true; }
  }
  const context = vm.createContext({ EventSource: FakeEventSource });
  vm.runInContext(source.replace(/^export\s+/gm, '') + '\nthis.subscribe = subscribeAdminSolutionEvents;', context);
  return { sources, subscribe: context.subscribe };
}

test('admin feed subscribes to BOTH owning databases through authenticated SSE routes', () => {
  const { sources, subscribe } = setup();
  const dispose = subscribe({});
  assert.deepEqual(sources.map(x => x.url), ['/api/admin/solution-events', '/api/admin/task-solution-events']);
  assert.ok(sources.every(x => x.options.withCredentials === true));
  dispose();
  assert.ok(sources.every(x => x.closed));
});

test('admin feed reports LIVE only while BOTH streams are open', () => {
  const { sources, subscribe } = setup();
  let live = 0, errors = 0;
  const dispose = subscribe({ onOpen: () => live++, onError: () => errors++ });
  sources[0].onopen();
  assert.equal(live, 0);
  sources[1].onopen();
  assert.equal(live, 1);
  sources[1].onerror({ message: 'Task service disconnected' });
  assert.equal(errors, 1);
  sources[1].onopen();
  assert.equal(live, 2);
  dispose();
  sources[0].onopen();
  assert.equal(live, 2);
});

test('both owners deliver solution events, malformed payloads are ignored, dispose blocks callbacks', () => {
  const { sources, subscribe } = setup();
  const kinds = [];
  const dispose = subscribe({ onEvent: event => kinds.push(event.kind) });
  sources[0].emit('solution', JSON.stringify({ kind: 'sql', itemId: 'one' }));
  sources[1].emit('solution', JSON.stringify({ kind: 'math', itemId: 'two' }));
  sources[1].emit('solution', '{broken-json');
  assert.deepEqual(kinds, ['sql', 'math']);
  dispose();
  sources[0].emit('solution', JSON.stringify({ kind: 'code' }));
  assert.deepEqual(kinds, ['sql', 'math']);
});
