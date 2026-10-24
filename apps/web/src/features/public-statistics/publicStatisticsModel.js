const validDay = (value) => /^\d{4}-\d{2}-\d{2}$/.test(String(value || ''));
const count = (value) => Math.max(0, Number(value) || 0);

export function toUtcDay(date) {
  return new Date(date).toISOString().slice(0, 10);
}

export function shortDay(day) {
  if (!validDay(day)) return '';
  const [, month, date] = day.split('-');
  return `${date}.${month}`;
}

// The server sends sparse UTC buckets. Fill missing days without changing the
// meaning of an omitted entry or using the viewer's local timezone as a key.
export function dailySeries(rows = [], days = 30, now = Date.now()) {
  const limit = [7, 30, 90].includes(days) ? days : 30;
  const source = new Map((Array.isArray(rows) ? rows : [])
    .filter((row) => validDay(row?.day)).map((row) => [row.day, row]));
  const today = new Date(now);
  const start = Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate() - limit + 1);
  return Array.from({ length: limit }, (_, i) => {
    const day = new Date(start + i * 86400000).toISOString().slice(0, 10);
    const row = source.get(day) || {};
    return {
      day,
      label: shortDay(day),
      accepted: count(row.accepted),
      failed: count(row.failed),
      pending: count(row.pending),
      tasks: count(row.tasks),
      rating: count(row.rating),
    };
  });
}

export function successPercent(accepted, failed) {
  const finished = count(accepted) + count(failed);
  return finished === 0 ? 0 : Math.round((count(accepted) / finished) * 100);
}

const languageNames = {
  cpp: 'C++', c: 'C', csharp: 'C#', java: 'Java', javascript: 'JavaScript',
  typescript: 'TypeScript', python: 'Python', sql: 'SQL', pascal: 'Pascal',
  kotlin: 'Kotlin', go: 'Go', rust: 'Rust', unknown: 'Не указан',
};
export function languageLabel(raw) {
  const key = String(raw || '').toLowerCase();
  return languageNames[key] || (key ? key.slice(0, 32) : languageNames.unknown);
}

const sourceNames = {
  code: 'Код и SQL', image: 'Изображения', task: 'Тесты и математика',
};
export function acceptedSources(rows = []) {
  return (Array.isArray(rows) ? rows : [])
    .map((row) => ({ label: sourceNames[row.kind] || 'Другие задания', value: count(row.acceptedAttempts) }))
    .filter((row) => row.value > 0);
}
