import { setAccessToken } from '../api/http';

export function extractApiErrorMessages(err, fallbackMessage) {
  const data = err?.response?.data;
  const rawErrors = data?.errors;

  const errorList = rawErrors && typeof rawErrors === 'object'
    ? Object.entries(rawErrors)
        .flatMap(([field, value]) => {
          const items = Array.isArray(value) ? value : [value];
          return items
            .map((x) => (typeof x === 'string' ? x.trim() : ''))
            .filter(Boolean)
            .map((msg) => (field && field !== '$' && field !== 'form' ? `${field}: ${msg}` : msg));
        })
        .filter(Boolean)
    : [];

  const primaryMessage =
    data?.message ||
    data?.error ||
    data?.detail ||
    (typeof data === 'string' ? data : null) ||
    err?.message ||
    fallbackMessage ||
    'Произошла ошибка';

  const messages = [primaryMessage, ...errorList].filter(Boolean);
  const uniqueMessages = [...new Set(messages)];

  return {
    status: err?.response?.status,
    primaryMessage,
    messages: uniqueMessages,
    userMessage: uniqueMessages.join('\n'),
    trace: data?.trace || null,
    path: data?.path || null,
  };
}

/**
 * Centralized error handler for API calls.
 * Given an error, a notify function, and an optional fallback message,
 * it shows appropriate toast notifications and performs global side effects (e.g. redirect on 401).
 */
export function handleApiError(err, notify, fallbackMessage) {
  try {
    const parsed = extractApiErrorMessages(err, fallbackMessage);
    const { status, primaryMessage, messages } = parsed;
    const serverMsg = primaryMessage;
    const combined = messages.join('\n');

    err.userMessage = combined;
    err.message = combined || err.message;

    if (status === 401) {
      // Unauthorized: inform the user, clear token, and redirect to login.
      notify.warn(serverMsg || 'Требуется вход в систему');
      // Remove any stored token so ProtectedRoute doesn't think we're authenticated
      setAccessToken(null);
      if (typeof window !== 'undefined' && window.location.pathname !== '/login') {
        window.location.assign('/login');
      }
      return parsed;
    }
    if (status === 403) {
      notify.error(serverMsg || 'Недостаточно прав');
      return parsed;
    }
    if (status === 404) {
      notify.warn(serverMsg || 'Не найдено');
      return parsed;
    }
    if (status === 429) {
      // quota exceeded: показываем дружелюбно (без "status code 429")
      notify.warn(serverMsg || 'Лимит исчерпан. Попробуйте позже.');
      return parsed;
    }
    if (status >= 500) {
      notify.error(serverMsg || fallbackMessage || 'Ошибка сервера');
      return parsed;
    }
    notify.error(serverMsg || fallbackMessage || 'Произошла ошибка');
    return parsed;
  } catch {
    notify.error(fallbackMessage || 'Произошла ошибка');
    return {
      status: null,
      primaryMessage: fallbackMessage || 'Произошла ошибка',
      messages: [fallbackMessage || 'Произошла ошибка'],
      userMessage: fallbackMessage || 'Произошла ошибка',
      trace: null,
      path: null,
    };
  }
}
