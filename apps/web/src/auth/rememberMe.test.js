import { accessExpiresAt, canRememberDevice, isRememberedDevice, REMEMBER_ME_STORAGE_KEY, storeRememberedDevice } from './rememberMe';

beforeEach(() => window.localStorage.clear());
afterEach(() => window.localStorage.clear());

test('remembering a device is opt-in, not an account/profile setting', () => {
  expect(isRememberedDevice()).toBe(false);
  expect(canRememberDevice()).toBe(true);
  expect(storeRememberedDevice(true)).toBe(true);
  expect(window.localStorage.getItem(REMEMBER_ME_STORAGE_KEY)).toBe('1');
  expect(isRememberedDevice()).toBe(true);
  expect(storeRememberedDevice(false)).toBe(true);
  expect(isRememberedDevice()).toBe(false);
  expect(window.localStorage.getItem(REMEMBER_ME_STORAGE_KEY)).toBeNull();
});

test('access-token expiration can be detected without refreshing', () => {
  const exp = Math.floor(Date.now() / 1000) + 120;
  const jwt = `ignored.${window.btoa(JSON.stringify({ exp }))}.signature`;
  expect(accessExpiresAt(jwt)).toBe(exp * 1000);
  expect(accessExpiresAt('invalid')).toBeNull();
});
