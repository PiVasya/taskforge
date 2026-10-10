export const BULK_DELETE_TABS = Object.freeze({
  code: { label: 'решения по коду', query: 'code-history' },
  sql: { label: 'SQL-решения', query: 'sql-history' },
  tests: { label: 'завершённые попытки тестов', query: 'tests' },
  images: { label: 'решения по картинкам', query: 'images' },
  math: { label: 'завершённые попытки по математике', query: 'math' },
});

export function bulkDeleteTarget(userId, tab) {
  const id = String(userId || '').trim();
  const config = Object.prototype.hasOwnProperty.call(BULK_DELETE_TABS, tab) ? BULK_DELETE_TABS[tab] : null;
  return id && config ? { userId: id, tab, ...config } : null;
}

export function bulkDeleteMessage(target, user) {
  if (!target) return '';
  const label = user?.email || user?.login || user?.displayName || target.userId;
  return `Удалить ВСЕ ${target.label} пользователя ${label} (ID: ${target.userId}) за всё время?\n\nЭто действие необратимо. Другие типы решений и другие пользователи не затрагиваются.`;
}
