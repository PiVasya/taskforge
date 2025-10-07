export function handleApiError(err, notify, fallbackMessage) {
  try {
    const status = err?.response?.status;
    const serverMsg = err?.response?.data?.message || err?.message;

    if (status === 401) {
      notify.warn("Требуется вход в систему");
      return;
    }
    if (status === 403) {
      notify.error(serverMsg || "Недостаточно прав");
      return;
    }
    if (status === 404) {
      notify.warn(serverMsg || "Не найдено");
      return;
    }
    if (status >= 500) {
      notify.error("Ошибка сервера. Попробуйте позже.");
      return;
    }
    notify.error(serverMsg || fallbackMessage || "Произошла ошибка");
  } catch {
    notify.error(fallbackMessage || "Произошла ошибка");
  }
}
