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

test('admin feed uses exactly one Solutions-owned authenticated SSE stream', () => {
  const { sources, subscribe } = setup();
  const dispose = subscribe({});
  assert.deepEqual(sources.map(x => x.url), ['/api/admin/solution-events']);
  assert.ok(sources.every(x => x.options.withCredentials === true));
  dispose();
  assert.ok(sources.every(x => x.closed));
});

test('admin feed reports LIVE when the sole Solutions stream is connected', () => {
  const { sources, subscribe } = setup();
  let live = 0, errors = 0;
  const dispose = subscribe({ onOpen: () => live++, onError: () => errors++ });
  sources[0].onopen();
  assert.equal(live, 1);
  sources[0].onerror({ message: 'Solutions service disconnected' });
  assert.equal(errors, 1);
  sources[0].onopen();
  assert.equal(live, 2);
  dispose();
  sources[0].onopen();
  assert.equal(live, 2);
});

test('Solutions delivers all five kinds, malformed payloads are ignored, dispose blocks callbacks', () => {
  const { sources, subscribe } = setup();
  const kinds = [];
  const dispose = subscribe({ onEvent: event => kinds.push(event.kind) });
  sources[0].emit('solution', JSON.stringify({ kind: 'sql', itemId: 'one' }));
  sources[0].emit('solution', JSON.stringify({ kind: 'math', itemId: 'two' }));
  sources[0].emit('solution', '{broken-json');
  assert.deepEqual(kinds, ['sql', 'math']);
  dispose();
  sources[0].emit('solution', JSON.stringify({ kind: 'code' }));
  assert.deepEqual(kinds, ['sql', 'math']);
});
