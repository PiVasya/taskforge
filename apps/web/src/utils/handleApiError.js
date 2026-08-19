import { getApiErrorMessage, normalizeApiError, setAccessToken } from '../api/http';
import { emitAuthRequired } from '../auth/authEvents';

function buildFallbackHowToFix(status, fallbackMessage) {
  if (status === 400 || status === 422) {
    return [
      'Проверьте обязательные поля и попробуйте ещё раз.',
      'Исправьте отмеченные поля, если они показаны на странице.',
    ];
  }
  if (status === 401) {
    return ['Войдите в систему заново.', 'После входа повторите действие.'];
  }
  if (status === 403) {
    return ['Проверьте, есть ли у вас нужная роль или доступ.', 'Если доступ должен быть, обратитесь к администратору.'];
  }
  if (status === 404) {
    return ['Обновите страницу и откройте раздел заново.', 'Проверьте, что объект ещё существует.'];
  }
  if (status === 409) {
    return ['Обновите страницу и проверьте текущие данные.', 'Повторите действие после обновления.'];
  }
  if (status === 429) {
    return ['Подождите немного и повторите попытку.'];
  }
  if (status >= 500) {
    return ['Попробуйте выполнить действие ещё раз чуть позже.', 'Если проблема повторяется, обратитесь к администратору.'];
  }
  return fallbackMessage ? ['Проверьте введённые данные и повторите действие.'] : [];
}

function asNotifier(input) {
  if (!input || input === false) return null;
  if (input?.notify === false) return null;
  if (typeof input?.warn === 'function' || typeof input?.error === 'function') return input;
  return null;
}

export function extractApiErrorMessages(err, fallbackMessage) {
  const normalized = err?.normalized || (() => {
    try { return normalizeApiError(err, fallbackMessage).normalized; } catch { return null; }
  })();

  if (normalized) {
    return {
      ...normalized,
      howToFix: normalized.howToFix?.length ? normalized.howToFix : buildFallbackHowToFix(normalized.status, fallbackMessage),
    };
  }

  const status = err?.response?.status || null;
  const primaryMessage = getApiErrorMessage(err, fallbackMessage || 'Не удалось выполнить действие');
  return {
    status,
    primaryMessage,
    messages: [primaryMessage],
    userMessage: primaryMessage,
    trace: null,
    traceId: null,
    path: null,
    code: null,
    userHint: null,
    howToFix: buildFallbackHowToFix(status, fallbackMessage),
    severity: status === 400 || status === 422 ? 'validation' : status === 401 || status === 403 || status === 404 || status === 409 || status === 429 ? 'warning' : 'error',
    fieldErrors: null,
  };
}

export function handleApiError(err, notifyOrOptions, fallbackMessage) {
  const parsed = extractApiErrorMessages(err, fallbackMessage);
  const notify = asNotifier(notifyOrOptions);

  if (err) {
    err.userMessage = parsed.userMessage || parsed.primaryMessage;
    err.message = parsed.primaryMessage;
  }

  if (parsed.status === 401) {
    setAccessToken(null);
    emitAuthRequired();
    if (notify) notify.warn?.(parsed.primaryMessage || 'Требуется вход в систему');
    return parsed;
  }

  if (!notify) return parsed;
  if (parsed.status === 403 || parsed.status === 404 || parsed.status === 409 || parsed.status === 429) {
    notify.warn?.(parsed.primaryMessage || 'Не удалось выполнить действие');
    return parsed;
  }
  notify.error?.(parsed.primaryMessage || fallbackMessage || 'Не удалось выполнить действие');
  return parsed;
}
