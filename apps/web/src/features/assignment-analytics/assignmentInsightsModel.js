import { solutionStatusLabel } from '../admin-solutions/adminSolutionLiveModel';

export const assignmentKindLabels = {
  code: 'Код',
  'code-test': 'Код',
  sql: 'SQL',
  test: 'Тест',
  math: 'Математика',
  image: 'Изображение',
  'image-trial': 'Изображение',
  'image-test': 'Изображение',
};

export const analyticsModeLabels = {
  off: 'Выключена',
  basic: 'Базовая',
  detailed: 'Подробная',
  solution: 'По решениям',
  custom: 'Настраиваемая',
  proctoring: 'Контроль поведения',
};

export const riskLevelLabels = {
  low: 'Низкий', medium: 'Средний', high: 'Высокий', critical: 'Критический',
};

export const behaviorEventLabels = {
  assignment_opened: 'Открыл задание',
  assignment_closed: 'Закрыл задание',
  submit_started: 'Начал отправку решения',
  submit_finished: 'Завершил отправку решения',
  submit_failed: 'Ошибка отправки решения',
  submit_passed: 'Решение принято',
  page_unloaded: 'Закрыл страницу',
  code_changed_aggregate: 'Серия изменений кода',
  visibility_hidden: 'Скрыл вкладку',
  visibility_visible: 'Вернулся на вкладку',
  window_blur: 'Переключился из окна',
  window_focus: 'Вернулся в окно',
  fullscreen_exit: 'Вышел из полноэкранного режима',
  fullscreen_enter: 'Включил полноэкранный режим',
  paste: 'Вставил текст',
  copy: 'Скопировал текст',
  cut: 'Вырезал текст',
  code_changed: 'Изменил код',
  test_answers_changed: 'Изменил ответы теста',
  test_answers_final: 'Отправил ответы теста',
  math_answers_changed: 'Изменил ответы по математике',
  math_answers_final: 'Отправил ответы по математике',
};

export const typeLabel = (kind) => assignmentKindLabels[String(kind || '').toLowerCase()] || String(kind || 'Решение');
export const behaviorLabel = (eventType) => behaviorEventLabels[String(eventType || '').toLowerCase()] || String(eventType || 'Событие').replaceAll('_', ' ');
export const statusLabel = (status, passed) => solutionStatusLabel(status) || (passed ? 'Принято' : 'Ожидает проверки');
export const riskLabel = (level) => riskLevelLabels[String(level || '').toLowerCase()] || 'Низкий';
export const riskReasonLabel = (reason) => String(reason || '')
  .replace(/\bfullscreen\b/gi, 'полноэкранного режима')
  .replace(/\bcopy\b/gi, 'копирование')
  .replace(/\bcut\b/gi, 'вырезание')
  .replace(/\bpaste\b/gi, 'вставка текста');

export function attemptSummary(data = {}) {
  const kinds = [
    { kind: 'code', label: 'Код и SQL', total: Number(data.codeAttempts || 0), passed: Number(data.passedCodeAttempts || 0), pending: Number(data.pendingCodeAttempts || 0) },
    { kind: 'test', label: 'Тесты', total: Number(data.testAttempts || 0), passed: Number(data.passedTests || 0) },
    { kind: 'math', label: 'Математика', total: Number(data.mathAttempts || 0), passed: Number(data.passedMath || 0) },
    { kind: 'image', label: 'Изображения', total: Number(data.imageAttempts || 0), passed: Number(data.passedImages || 0) },
  ].map((item) => ({ ...item, pending: Math.max(0, Number(item.pending || 0)), failed: Math.max(0, item.total - item.passed - Number(item.pending || 0)) }));
  const total = kinds.reduce((sum, item) => sum + item.total, 0);
  const passed = kinds.reduce((sum, item) => sum + item.passed, 0);
  const pending = kinds.reduce((sum, item) => sum + item.pending, 0);
  return { kinds, total, passed, pending, failed: Math.max(0, total - passed - pending), successRate: total ? Math.round(passed * 1000 / total) / 10 : 0 };
}

export function dailyChartRows(dailyAttempts = [], days = 30, today = new Date()) {
  const limit = Math.min(Math.max(1, Number(days) || 30), 90);
  const dayKey = (date) => date.toISOString().slice(0, 10);
  const end = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate()));
  const byDate = new Map((dailyAttempts || []).map((item) => [String(item.dayUtc || item.date || '').slice(0, 10), item]));
  return Array.from({ length: limit }, (_, index) => {
    const date = new Date(end.getTime() - (limit - 1 - index) * 86400000);
    const dayUtc = dayKey(date);
    const source = byDate.get(dayUtc) || {};
    const passed = Math.max(0, Number(source.passed || 0));
    const total = Math.max(0, Number(source.total || 0));
    const pending = Math.max(0, Number(source.pending || 0));
    const failed = Math.max(0, Number(source.failed ?? (total - passed - pending)));
    return {
      dayUtc,
      label: new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: 'short', timeZone: 'UTC' }).format(date),
      passed,
      failed,
      pending,
      total,
    };
  });
}

export function normalizeHistoricalAttempts(rows = [], assignmentId, assignmentTitle) {
  return (rows || []).filter((row) => row.attemptId).map((row) => ({
    key: `${String(row.sourceKind || row.kind || 'code').toLowerCase()}:${row.attemptId}`,
    kind: String(row.sourceKind || row.kind || 'code').toLowerCase(),
    itemId: row.attemptId,
    userId: row.userId,
    assignmentId,
    assignmentTitle,
    userLabel: row.fullName || row.displayName || row.login || row.email || 'Пользователь',
    occurredAtUtc: row.submittedAtUtc || row.createdAtUtc,
    status: row.status || (row.passed ? 'passed' : 'failed'),
    score: row.scorePercent,
    language: row.language,
    hasCode: row.hasCode,
    detail: null,
    isHistorical: true,
  }));
}

export function mergeAttemptFeed(historical = [], live = [], assignmentId, maxRows = 120) {
  const byKey = new Map();
  for (const row of historical) {
    if (!row?.key) continue;
    byKey.set(row.key, row);
  }
  for (const row of live) {
    if (!row?.key || String(row.assignmentId || '').toLowerCase() !== String(assignmentId || '').toLowerCase()) continue;
    const older = byKey.get(row.key);
    // SSE has up-to-date verdicts. Preserve stable labels from historical
    // records when a live event has not yet loaded its metadata.
    byKey.set(row.key, { ...older, ...row, userLabel: row.userLabel || older?.userLabel });
  }
  return Array.from(byKey.values())
    .sort((a, b) => Date.parse(b.occurredAtUtc || 0) - Date.parse(a.occurredAtUtc || 0))
    .slice(0, maxRows);
}
