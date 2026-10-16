import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const webRoot = new URL('../../', import.meta.url);
const sourceFile = path => path.startsWith('apps/web/')
  ? new URL(path.slice('apps/web/'.length), webRoot)
  : new URL(`../web-ct/${path.slice('apps/web-ct/'.length)}`, webRoot);
const applications = ['web'];
try { await fs.access(sourceFile('apps/web-ct/src/auth/cookieSession.js')); applications.push('web-ct'); } catch {}

let sequence = 0;
const moduleUrl = source => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;
async function sourceUrl(path, imports = {}) {
  let source = await fs.readFile(sourceFile(path), 'utf8');
  for (const [name, url] of Object.entries(imports)) {
    source = source.replaceAll(`from '${name}'`, `from '${url}'`).replaceAll(`from "${name}"`, `from "${url}"`);
  }
  return moduleUrl(`${source}\n// test instance ${++sequence}\n`);
}
const info = (extra = {}) => ({ authenticated: true, expiresAtUtc: new Date(Date.now() + 3600000).toISOString(), rememberMe: true, user: { id: 'user-1', role: 'User' }, ...extra });
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };

function browser(t) {
  const previous = { window: globalThis.window, fetch: globalThis.fetch };
  const store = new Map();
  const win = Object.assign(new EventTarget(), {
    location: { origin: 'https://taskforge.test' },
    localStorage: { getItem: key => store.get(key) ?? null, setItem: (key, value) => store.set(key, String(value)), removeItem: key => store.delete(key) },
  });
  const sent = [];
  globalThis.window = win;
  globalThis.fetch = async (url, options) => { sent.push({ url, options }); return { ok: true, status: 200 }; };
  t.after(() => { globalThis.window = previous.window; globalThis.fetch = previous.fetch; });
  return { win, store, sent };
}

for (const app of applications) {
  const coreUrl = await sourceUrl(`apps/${app}/src/auth/cookieSession.js`);
  const core = await import(coreUrl);
  const roles = await import(await sourceUrl(`apps/${app}/src/auth/profileRoles.js`));

  test(`${app}: session metadata excludes credentials and rejects legacy token-only responses`, () => {
    const result = core.readSessionInfo(info({ accessToken: 'must-not-survive', refreshToken: 'must-not-survive', extra: 42 }));
    assert.deepEqual(Object.keys(result).sort(), ['authenticated', 'expiresAtUtc', 'rememberMe', 'user']);
    assert.equal(core.sessionExpiresAt(result), Date.parse(result.expiresAtUtc));
    assert.equal(core.sessionExpiresAt({ expiresAtUtc: 'invalid' }), null);
    assert.throws(() => core.readSessionInfo({ accessToken: 'legacy-token', user: {} }));
  });

  test(`${app}: HTTP requests use credentials and discard all case variants of Authorization`, () => {
    const config = core.prepareCookieRequest({ method: 'patch', withCredentials: false, headers: { aUtHoRiZaTiOn: 'old', Authorization: 'old-2' } });
    assert.equal(config.withCredentials, true);
    assert.deepEqual(config.headers, { 'X-TaskForge-CSRF': '1' });
    const safe = core.prepareCookieRequest({ method: 'GET' });
    assert.deepEqual(safe.headers, {});
  });

  test(`${app}: AxiosHeaders API receives deletion and CSRF header`, () => {
    const calls = [];
    const headers = { delete: name => calls.push(['delete', name]), set: (name, value) => calls.push(['set', name, value]) };
    core.prepareCookieRequest({ method: 'POST', headers });
    assert.deepEqual(calls, [['delete', 'Authorization'], ['set', 'X-TaskForge-CSRF', '1']]);
  });

  test(`${app}: roles come from the server profile including SuperAdmin inheritance`, () => {
    assert.deepEqual(roles.rolesFromProfile(null), []);
    assert.deepEqual(roles.rolesFromProfile({ role: 'SuperAdmin', roles: ['superadmin', 'Minecraft'] }), ['SuperAdmin', 'Minecraft', 'Admin']);
    assert.deepEqual(roles.rolesFromProfile({ role: 'User', roles: 'Editor;User' }), ['User', 'Editor']);
  });

  test(`${app}: concurrent refreshes are one request and publish only metadata`, async () => {
    const pending = deferred(); const calls = []; const updates = [];
    const client = core.createCookieSessionClient((url) => { calls.push(url); return pending.promise; }, value => updates.push(value));
    const first = client.refresh(); const second = client.refresh();
    assert.equal(first, second);
    await Promise.resolve();
    assert.deepEqual(calls, ['/api/auth/refresh']);
    pending.resolve(info({ accessToken: 'ignored' }));
    await first;
    assert.equal(updates.length, 1);
    assert.equal('accessToken' in updates[0], false);
  });

  test(`${app}: logout waits for an in-flight refresh and rejects new refreshes`, async () => {
    const pending = deferred(); const calls = [];
    const client = core.createCookieSessionClient(url => {
      calls.push(url);
      return url.endsWith('/refresh') ? pending.promise : Promise.resolve({ message: 'ok' });
    });
    const refreshing = client.refresh();
    await Promise.resolve();
    const logout = client.logout();
    assert.equal(logout, client.logout());
    await assert.rejects(client.refresh(), /Logout/);
    assert.deepEqual(calls, ['/api/auth/refresh']);
    pending.resolve(info());
    await refreshing; await logout;
    assert.deepEqual(calls, ['/api/auth/refresh', '/api/auth/logout']);
  });

  test(`${app}: a failed logout is observable and does not poison later requests`, async () => {
    let fails = true;
    const client = core.createCookieSessionClient(url => {
      if (url.endsWith('/logout') && fails) return Promise.reject(new Error('offline'));
      return Promise.resolve(url.endsWith('/login') ? info() : { message: 'ok' });
    });
    await assert.rejects(client.logout(), /offline/);
    fails = false;
    assert.equal((await client.login({ login: 'user' })).authenticated, true);
    await client.logout();
  });

  test(`${app}: cross-tab messages contain no identity or credential`, t => {
    const { store } = browser(t);
    core.publishSessionChange('logout');
    const value = JSON.parse(store.get(core.SESSION_STORAGE_EVENT_KEY));
    assert.deepEqual(Object.keys(value).sort(), ['kind', 'nonce']);
    assert.equal(value.kind, 'logout');
  });

  async function httpClient(t, { remembered = true, acceptAfterRefresh = true, refreshError = false } = {}) {
    const state = browser(t);
    state.win.localStorage.setItem('taskforge.auth.remember-me.v1', remembered ? '1' : '0');
    let requestHandler, successHandler, errorHandler, validCookie = false;
    const requests = [];
    const client = async request => {
      const config = await requestHandler({ ...request, headers: { ...request.headers } });
      requests.push(config);
      if (config.url === '/api/auth/refresh') {
        await new Promise(resolve => setTimeout(resolve, 5));
        if (refreshError) return errorHandler({ config, response: { status: 503, data: {}, headers: {} } });
        validCookie = true;
        return successHandler({ status: 200, headers: {}, config, data: info() });
      }
      if (validCookie && acceptAfterRefresh) return successHandler({ status: 200, headers: {}, config, data: { ok: true } });
      return errorHandler({ config, response: { status: 401, data: { message: 'Unauthorized' }, headers: {} } });
    };
    client.post = (url, data, options = {}) => client({ url, data, method: 'POST', ...options });
    client.interceptors = { request: { use: fn => { requestHandler = fn; } }, response: { use: (yes, no) => { successHandler = yes; errorHandler = no; } } };
    const key = `__taskforgeHttpTest${++sequence}`;
    globalThis[key] = { create: () => client };
    t.after(() => { delete globalThis[key]; });
    const imports = {
      axios: moduleUrl(`export default globalThis.${key};`),
      '../auth/rememberMe': await sourceUrl(`apps/${app}/src/auth/rememberMe.js`),
      '../auth/cookieSession': coreUrl,
      '../devtools/frontendDiagnostics': moduleUrl('export function logFrontendEvent() {}'),
    };
    const module = await import(await sourceUrl(`apps/${app}/src/api/http.js`, imports));
    return { ...state, ...module, requests };
  }

  test(`${app}: real HTTP interceptors refresh a cookie and replay once without Bearer`, async t => {
    const { default: api, requests } = await httpClient(t);
    const result = await api({ url: '/api/private', method: 'GET', headers: { Authorization: 'Bearer legacy' } });
    assert.equal(result.data.ok, true);
    assert.deepEqual(requests.map(request => request.url), ['/api/private', '/api/auth/refresh', '/api/private']);
    for (const request of requests) { assert.equal(request.headers.Authorization, undefined); assert.equal(request.withCredentials, true); }
    assert.equal(requests[1].headers['X-TaskForge-CSRF'], '1');
  });

  test(`${app}: real HTTP interceptors bound repeated 401 responses`, async t => {
    const { default: api, requests } = await httpClient(t, { acceptAfterRefresh: false });
    await assert.rejects(api({ url: '/api/private' }), error => error.response.status === 401);
    assert.deepEqual(requests.map(request => request.url), ['/api/private', '/api/auth/refresh', '/api/private']);
  });

  test(`${app}: no opt-in means no silent refresh`, async t => {
    const { default: api, requests } = await httpClient(t, { remembered: false });
    await assert.rejects(api({ url: '/api/private' }), error => error.response.status === 401);
    assert.equal(requests.length, 1);
  });

  test(`${app}: concurrent HTTP 401s share the refresh and both retry with cookies`, async t => {
    const { default: api, requests } = await httpClient(t);
    await Promise.all([api({ url: '/api/a' }), api({ url: '/api/b' })]);
    assert.equal(requests.filter(request => request.url === '/api/auth/refresh').length, 1);
    assert.equal(requests.length, 5);
  });

  test(`${app}: failed refresh rejects every queued caller without recursion`, async t => {
    const { default: api, requests } = await httpClient(t, { refreshError: true });
    const results = await Promise.allSettled([api({ url: '/api/a' }), api({ url: '/api/b' })]);
    assert.equal(results.every(result => result.status === 'rejected' && result.reason.response.status === 503), true);
    assert.equal(requests.length, 3);
  });
}

test('logout stops every registered authenticated realtime connection even when one stop throws', async () => {
  const registry = await import(await sourceUrl('apps/web/src/realtime/connectionRegistry.js'));
  const calls = [];
  registry.registerAuthenticatedConnection({ stop() { calls.push('one'); throw new Error('already disconnected'); } }, () => calls.push('dispose-one'));
  registry.registerAuthenticatedConnection({ stop() { calls.push('two'); return Promise.resolve(); } }, () => calls.push('dispose-two'));
  registry.stopAuthenticatedConnections();
  registry.stopAuthenticatedConnections();
  assert.deepEqual(calls, ['dispose-one', 'one', 'dispose-two', 'two']);
});

test('unload activity uses cookie fetch with the CSRF header instead of an unprotectable beacon', async t => {
  const { sent } = browser(t);
  const url = await sourceUrl('apps/web/src/api/assignmentActivity.js', {
    './http': moduleUrl('export default {};'),
    '../auth/cookieSession': await sourceUrl('apps/web/src/auth/cookieSession.js'),
    '../devtools/frontendDiagnostics': moduleUrl('export function logFrontendEvent() {}'),
  });
  const api = await import(url);
  assert.equal(api.sendAssignmentActivityBeacon('task-1', { items: [] }), true);
  assert.equal(sent.length, 1);
  assert.equal(sent[0].options.credentials, 'include');
  assert.equal(sent[0].options.keepalive, true);
  assert.equal(sent[0].options.headers['X-TaskForge-CSRF'], '1');
  assert.equal(sent[0].options.headers.Authorization, undefined);
});

async function hubModules(t, path) {
  const registryUrl = await sourceUrl('apps/web/src/realtime/connectionRegistry.js');
  const registry = await import(registryUrl);
  const built = [];
  class HubConnectionBuilder {
    withUrl(url, options) { this.url = url; this.options = options; return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() {
      const closed = [];
      const connection = {
        url: this.url, options: this.options, state: 'Disconnected', stops: 0, invokes: [],
        on() {}, onreconnecting() {}, onreconnected() {}, onclose(callback) { closed.push(callback); },
        async start() { this.state = 'Connected'; },
        async invoke(...args) { this.invokes.push(args); },
        async stop() { this.stops += 1; this.state = 'Disconnected'; closed.forEach(callback => callback()); },
      };
      built.push(connection);
      return connection;
    }
  }
  const key = `__taskforgeHubTest${++sequence}`;
  globalThis[key] = { HubConnectionBuilder, HubConnectionState: { Connected: 'Connected' }, LogLevel: { None: 0, Warning: 3 } };
  t.after(() => { registry.stopAuthenticatedConnections(); delete globalThis[key]; });
  const url = await sourceUrl(path, {
    '@microsoft/signalr': moduleUrl(`export const {HubConnectionBuilder,HubConnectionState,LogLevel}=globalThis.${key};`),
    '../auth/cookieSession': await sourceUrl('apps/web/src/auth/cookieSession.js'),
    './connectionRegistry': registryUrl,
    '../realtime/connectionRegistry': registryUrl,
    '../devtools/frontendDiagnostics': moduleUrl('export const instrumentSignalRConnection = value => value; export function logFrontendEvent() {}'),
    './http': moduleUrl('export default {};'),
  });
  return { module: await import(url), registry, built };
}

for (const [file, get, ensure, stop, path] of [
  ['agentHub', 'getAgentHub', 'ensureAgentHubStarted', 'stopAgentHub', '/hubs/agent'],
  ['supportHub', 'getSupportHub', 'ensureSupportHubStarted', 'stopSupportHub', '/hubs/support'],
  ['minecraftChatHub', 'getMinecraftChatHub', 'ensureMinecraftChatHubStarted', null, '/hubs/minecraft-chat'],
]) {
  test(`${file}: real builder uses only cookie credentials and logout resets the singleton`, async t => {
    const { module, registry, built } = await hubModules(t, `apps/web/src/realtime/${file}.js`);
    const first = await module[ensure]();
    assert.equal(first.url, path);
    assert.deepEqual(first.options, { withCredentials: true, headers: { 'X-TaskForge-CSRF': '1' } });
    assert.equal(module[get](), first);
    registry.stopAuthenticatedConnections();
    assert.equal(first.stops, 1);
    const second = await module[ensure]();
    assert.notEqual(first, second);
    assert.equal(built.length, 2);
    if (stop) {
      await module[stop]();
      registry.stopAuthenticatedConnections();
      assert.equal(second.stops, 1);
    }
  });
}

test('course-map connections are keyed by user/course rather than by raw credentials', async t => {
  const { module, registry } = await hubModules(t, 'apps/web/src/realtime/courseMapHub.js');
  const first = module.createCourseMapPresenceConnection('user-a', 'course-1');
  const other = module.createCourseMapPresenceConnection('user-b', 'course-1');
  assert.equal(first.url, '/hubs/course-map');
  assert.deepEqual(first.options, { withCredentials: true, headers: { 'X-TaskForge-CSRF': '1' } });
  assert.notEqual(first, other);
  assert.equal(module.createCourseMapPresenceConnection('user-a', 'course-1'), first);
  await module.disposeCourseMapPresenceConnection('user-a', 'course-1');
  registry.stopAuthenticatedConnections();
  assert.equal(first.stops, 1);
  assert.equal(other.stops, 1);
  assert.notEqual(module.createCourseMapPresenceConnection('user-b', 'course-1'), other);
});

test('assignment analytics uses a cookie-only connection registered for logout', async t => {
  browser(t);
  const { module, registry } = await hubModules(t, 'apps/web/src/api/adminAssignmentInsights.js');
  const connection = module.createAssignmentAnalyticsConnection('task-1');
  assert.equal(connection.url, '/hubs/assignment-analytics');
  assert.deepEqual(connection.options, { withCredentials: true, headers: { 'X-TaskForge-CSRF': '1' } });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(connection.invokes, [['JoinAssignment', 'task-1']]);
  registry.stopAuthenticatedConnections();
  assert.equal(connection.stops, 1);
});
