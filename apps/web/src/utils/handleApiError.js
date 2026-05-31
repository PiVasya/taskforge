import { setAccessToken } from '../api/http';

function buildFallbackHowToFix(status, fallbackMessage) {
  if (status === 400) {
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
    return ['Обновите страницу и проверьте, что объект ещё существует.', 'Если вы перешли по старой ссылке, откройте раздел заново.'];
  }
  if (status === 409) {
    return ['Обновите страницу и проверьте текущие данные.', 'Повторите действие после обновления.'];
  }
  if (status === 429) {
    return ['Подождите немного и повторите попытку.', 'Если лимит не должен был сработать, обратитесь к администратору.'];
  }
  if (status >= 500) {
    return [
      'Попробуйте выполнить действие ещё раз чуть позже.',
      'Если ошибка повторяется, передайте администратору код ошибки или Trace из блока ниже.',
    ];
  }
  return fallbackMessage ? ['Проверьте введённые данные и повторите действие.'] : [];
}

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

  const detail = data?.detail && data?.detail !== primaryMessage ? data.detail : null;
  const path = data?.path || null;
  const traceId = data?.traceId || data?.trace || null;
  const code = data?.code || null;
  const userHint = data?.userHint || null;
  const severity = data?.severity || (err?.response?.status === 400 ? 'validation' : err?.response?.status >= 500 ? 'error' : 'warning');
  const howToFix = Array.isArray(data?.howToFix)
    ? data.howToFix.filter((x) => typeof x === 'string' && x.trim()).map((x) => x.trim())
    : buildFallbackHowToFix(err?.response?.status, fallbackMessage);

  const messages = [primaryMessage, detail, ...errorList].filter(Boolean);
  const uniqueMessages = [...new Set(messages)];

  return {
    status: err?.response?.status,
    primaryMessage,
    messages: uniqueMessages,
    userMessage: uniqueMessages.join('\n'),
    trace: traceId,
    traceId,
    path,
    code,
    userHint,
    howToFix,
    severity,
    fieldErrors: rawErrors || null,
  };
}

export function handleApiError(err, notify, fallbackMessage) {
  try {
    const parsed = extractApiErrorMessages(err, fallbackMessage);
    const { status, primaryMessage, messages } = parsed;
    const serverMsg = primaryMessage;
    const combined = messages.join('\n');

    err.userMessage = combined;
    err.message = combined || err.message;

    if (status === 401) {
      notify.warn(serverMsg || 'Требуется вход в систему');
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
      traceId: null,
      path: null,
      code: null,
      userHint: null,
      howToFix: buildFallbackHowToFix(null, fallbackMessage),
      severity: 'error',
      fieldErrors: null,
    };
  }
}
