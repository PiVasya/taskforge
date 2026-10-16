// Exercise the real HTTP client interceptors with a deterministic transport.
// No server or browser credentials are needed for these regression tests.
function createClient({ remembered, succeedAfterRefresh = false }) {
  let requestInterceptor;
  let responseErrorInterceptor;
  const requests = [];
  let protectedCalls = 0;
  let cookieAccepted = false;

  const client = jest.fn(async (request) => {
    const config = requestInterceptor({ ...request, headers: { ...request.headers } });
    requests.push({ url: config.url, authorization: config.headers.Authorization });
    if (config.url === '/api/auth/refresh') {
      cookieAccepted = true;
      return { data: { authenticated: true, expiresAtUtc: new Date(Date.now() + 60000).toISOString(), rememberMe: true, user: { id: 'user-1', role: 'User' } }, config };
    }
    protectedCalls += 1;
    if (protectedCalls > 5) throw new Error('refresh retry loop');
    if (succeedAfterRefresh && cookieAccepted) {
      return { data: { ok: true }, config };
    }
    return responseErrorInterceptor({
      config,
      response: { status: 401, data: { message: 'Unauthorized' }, headers: {} },
    });
  });
  client.post = (url, data, options = {}) => client({ url, data, method: 'POST', ...options });
  client.interceptors = {
    request: { use: (handler) => { requestInterceptor = handler; } },
    response: { use: (_success, onError) => { responseErrorInterceptor = onError; } },
  };

  jest.doMock('axios', () => ({ __esModule: true, default: { create: () => client } }));
  jest.doMock('../auth/rememberMe', () => ({ isRememberedDevice: () => remembered }));
  const { default: api } = require('./http');
  return { api, requests };
}

beforeEach(() => jest.resetModules());
afterEach(() => { jest.dontMock('axios'); jest.dontMock('../auth/rememberMe'); });

test('a remembered session retries once using the renewed cookie', async () => {
  const { api, requests } = createClient({ remembered: true, succeedAfterRefresh: true });
  await expect(api({ url: '/api/private', method: 'GET', headers: { Authorization: 'Bearer expired-access' } }))
    .resolves.toMatchObject({ data: { ok: true } });
  expect(requests.map((r) => r.url)).toEqual(['/api/private', '/api/auth/refresh', '/api/private']);
  expect(requests.every(request => request.authorization === undefined)).toBe(true);
});

test('a repeatedly rejected request never triggers a second refresh', async () => {
  const { api, requests } = createClient({ remembered: true });
  await expect(api({ url: '/api/private', method: 'GET' })).rejects.toMatchObject({ response: { status: 401 } });
  expect(requests.filter((r) => r.url === '/api/private')).toHaveLength(2);
  expect(requests.filter((r) => r.url === '/api/auth/refresh')).toHaveLength(1);
});

test('without opt-in, a 401 never initiates a refresh', async () => {
  const { api, requests } = createClient({ remembered: false });
  await expect(api({ url: '/api/private', method: 'GET' })).rejects.toMatchObject({ response: { status: 401 } });
  expect(requests.map((r) => r.url)).toEqual(['/api/private']);
});
