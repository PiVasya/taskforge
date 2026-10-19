export function filterLiveItems(items, { userId = '', groupId = '', groupUserIds = [], filterDays = 0, now = Date.now() } = {}) {
  const groupSet = new Set((groupUserIds || []).map((id) => String(id)));
  const days = Number(filterDays || 0);
  const since = days > 0 ? Number(now) - days * 24 * 60 * 60 * 1000 : null;

  return (items || []).filter((item) => {
    if (userId && String(item?.userId || '') !== String(userId)) return false;
    if (groupId && !groupSet.has(String(item?.userId || ''))) return false;
    if (since && new Date(item?.occurredAtUtc || 0).getTime() < since) return false;
    return true;
  });
}

export function filterLiveItemsByTab(items, tab) {
  const value = String(tab || 'live').toLowerCase();
  if (value === 'groups') return [];
  if (value === 'code' || value === 'sql') return (items || []).filter((item) => String(item?.kind || '').toLowerCase() === value);
  if (value === 'tests') return (items || []).filter((item) => String(item?.kind || '').toLowerCase() === 'test');
  if (value === 'images') return (items || []).filter((item) => String(item?.kind || '').toLowerCase() === 'image');
  if (value === 'math') return (items || []).filter((item) => String(item?.kind || '').toLowerCase() === 'math');
  return items || [];
}

export function solutionLiveStateLabel(state) {
  if (state === 'live') return 'В эфире';
  if (state === 'reconnecting') return 'Переподключение';
  return 'Подключение';
}

export function solutionKindLabel(kind) {
  const value = String(kind || '').toLowerCase();
  if (value === 'sql') return 'SQL';
  if (value === 'test') return 'Тест';
  if (value === 'math') return 'Математика';
  if (value === 'image') return 'Картинка';
  return 'Код';
}

export function solutionStatusLabel(status) {
  const value = String(status || '').trim().toLowerCase();
  if (!value) return null;
  if (['accepted', 'passed', 'success'].includes(value)) return 'Принято';
  if (['rejected', 'wronganswer', 'wrong_answer', 'failed'].includes(value)) return 'Не принято';
  if (['preparing', 'queued', 'running', 'pending', 'judging'].includes(value)) return 'Проверяется';
  if (value === 'compileerror' || value === 'compile_error') return 'Ошибка компиляции';
  if (value === 'runtimeerror' || value === 'runtime_error') return 'Ошибка выполнения';
  if (value === 'timelimitexceeded' || value === 'time_limit_exceeded') return 'Лимит времени';
  if (value === 'outputlimitexceeded' || value === 'output_limit_exceeded') return 'Лимит вывода';
  if (value === 'judgeunavailable' || value === 'judge_unavailable') return 'Проверка недоступна';
  if (value === 'policyfailed' || value === 'policy_failed') return 'Отклонено';
  return status;
}

export function isPendingLiveStatus(status) {
  return ['preparing', 'queued', 'running', 'pending', 'judging'].includes(String(status || '').trim().toLowerCase());
}

export function reconcileLiveDetail(item, detail) {
  if (!item || !detail || !isPendingLiveStatus(item.status)) return item;
  const status = detail.status || detail.verdict || null;
  if (!status) return item;
  return {
    ...item,
    detail,
    status,
    score: detail.score ?? detail.scorePercent ?? detail.similarityPercent ?? item.score ?? null,
  };
}

// An SSE event can arrive after its HTTP detail request, or without the detail at all.
// An immutable code submission cannot move back from a definitive verdict.
export function mergeLiveEvent(existing, incoming) {
  if (!existing || isPendingLiveStatus(existing.status)) return incoming;
  const nextStatus = String(incoming.status || '').trim().toLowerCase();
  const oldStatus = String(existing.status || '').trim().toLowerCase();
  const oldIsCode = String(existing.kind || incoming.kind || '').toLowerCase() === 'code';
  const weak = (value) => value === 'judgeunavailable' || value === 'judge_unavailable';
  const preserveTerminal = !nextStatus || isPendingLiveStatus(nextStatus) ||
    (oldIsCode && !weak(oldStatus) && oldStatus !== nextStatus);
  if (!preserveTerminal) return incoming;
  return { ...incoming, status: existing.status, score: existing.score, detail: existing.detail ?? incoming.detail };
}
