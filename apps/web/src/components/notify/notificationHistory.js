export const NOTIFICATION_HISTORY_STORAGE_KEY = 'taskforge.notifications.v1';
export const NOTIFICATION_HISTORY_CLEARED_EVENT = 'taskforge:notifications-cleared';

const VALID_TYPES = new Set(['success', 'error', 'warning', 'info']);

function normalizeNotification(item) {
  if (!item || typeof item !== 'object') return null;
  const message = typeof item.message === 'string' ? item.message.trim() : '';
  if (!message) return null;

  const createdAt = Number(item.createdAt);
  return {
    id: String(item.id || `${Number.isFinite(createdAt) ? createdAt : Date.now()}-${Math.random()}`),
    message,
    type: VALID_TYPES.has(item.type) ? item.type : 'info',
    createdAt: Number.isFinite(createdAt) ? createdAt : Date.now(),
    read: Boolean(item.read),
  };
}

export function loadNotificationHistory() {
  if (typeof window === 'undefined') return [];
  try {
    const raw = window.localStorage.getItem(NOTIFICATION_HISTORY_STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.map(normalizeNotification).filter(Boolean);
  } catch {
    return [];
  }
}

export function saveNotificationHistory(items) {
  if (typeof window === 'undefined') return;
  try {
    const normalized = Array.isArray(items)
      ? items.map(normalizeNotification).filter(Boolean)
      : [];
    if (normalized.length === 0) {
      window.localStorage.removeItem(NOTIFICATION_HISTORY_STORAGE_KEY);
      return;
    }
    window.localStorage.setItem(NOTIFICATION_HISTORY_STORAGE_KEY, JSON.stringify(normalized));
  } catch {}
}

export function clearNotificationHistory({ broadcast = true } = {}) {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.removeItem(NOTIFICATION_HISTORY_STORAGE_KEY);
  } catch {}
  if (broadcast) {
    try {
      window.dispatchEvent(new Event(NOTIFICATION_HISTORY_CLEARED_EVENT));
    } catch {}
  }
}
