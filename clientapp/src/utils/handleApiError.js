import { setAccessToken } from '../api/http';

/**
 * Centralized error handler for API calls.
 * Given an error, a notify function, and an optional fallback message,
 * it shows appropriate toast notifications and performs global side effects (e.g. redirect on 401).
 */
export function handleApiError(err, notify, fallbackMessage) {
  try {
    const status = err?.response?.status;
    const serverMsg =
      err?.response?.data?.message ||
      (typeof err?.response?.data === 'string' ? err.response.data : null) ||
      err?.message;

    if (status === 401) {
      // Unauthorized: inform the user, clear token, and redirect to login.
      notify.warn(serverMsg || 'Требуется вход в систему');
      // Remove any stored token so ProtectedRoute doesn't think we're authenticated
      setAccessToken(null);
      if (typeof window !== 'undefined' && window.location.pathname !== '/login') {
        window.location.assign('/login');
      }
      return;
    }
    if (status === 403) {
      notify.error(serverMsg || 'Недостаточно прав');
      return;
    }
    if (status === 404) {
      notify.warn(serverMsg || 'Не найдено');
      return;
    }
    if (status >= 500) {
      notify.error('Ошибка сервера. Попробуйте позже.');
      return;
    }
    notify.error(serverMsg || fallbackMessage || 'Произошла ошибка');
  } catch {
    notify.error(fallbackMessage || 'Произошла ошибка');
  }
}
