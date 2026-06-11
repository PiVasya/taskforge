export function sanitizeRunnerText(value) {
  if (value == null) return '';
  const s = String(value);
  if (/fork\/exec/i.test(s) && /permission denied/i.test(s)) {
    return 'Не удалось запустить программу: нет прав на выполнение файла проверки.';
  }
  return s
    .replace(/\/tmp\/taskforge-[^\s:]+/g, '[временный файл]')
    .replace(/\/tmp\/go-build[^\s:]+/g, '[временный файл]')
    .replace(/\/app\/[^\s:]+/g, '[внутренний файл]');
}
