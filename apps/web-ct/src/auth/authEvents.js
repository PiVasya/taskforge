export const AUTH_REQUIRED_EVENT = 'taskforge:auth-required';

export function emitAuthRequired() {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new CustomEvent(AUTH_REQUIRED_EVENT));
}
