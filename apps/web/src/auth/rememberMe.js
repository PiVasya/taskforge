// A browser-local preference: never synchronize it with profile or UI settings.
export const REMEMBER_ME_STORAGE_KEY = 'taskforge.auth.remember-me.v1';

export function isRememberedDevice() {
  try {
    return typeof window !== 'undefined' && window.localStorage.getItem(REMEMBER_ME_STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}

export function storeRememberedDevice(enabled) {
  if (typeof window === 'undefined') return false;
  try {
    if (enabled) window.localStorage.setItem(REMEMBER_ME_STORAGE_KEY, '1');
    else window.localStorage.removeItem(REMEMBER_ME_STORAGE_KEY);
    return true;
  } catch {
    return false;
  }
}

export function canRememberDevice() {
  if (typeof window === 'undefined') return false;
  try {
    const probe = `${REMEMBER_ME_STORAGE_KEY}:probe`;
    window.localStorage.setItem(probe, '1');
    window.localStorage.removeItem(probe);
    return true;
  } catch {
    return false;
  }
}

export function accessExpiresAt(token) {
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const expiry = Number(JSON.parse(window.atob(payload)).exp);
    return Number.isFinite(expiry) && expiry > 0 ? expiry * 1000 : null;
  } catch {
    return null;
  }
}
