import {
  clearNotificationHistory,
  loadNotificationHistory,
  NOTIFICATION_HISTORY_CLEARED_EVENT,
  NOTIFICATION_HISTORY_STORAGE_KEY,
  saveNotificationHistory,
} from './notificationHistory';

describe('notification history storage', () => {
  beforeEach(() => window.localStorage.clear());
  afterEach(() => window.localStorage.clear());

  test('persists serializable notification history', () => {
    saveNotificationHistory([
      { id: 'n1', message: 'Готово', type: 'success', createdAt: 123, read: false },
      { id: 'n2', message: 'Осторожно', type: 'warning', createdAt: 456, read: true },
    ]);

    expect(loadNotificationHistory()).toEqual([
      { id: 'n1', message: 'Готово', type: 'success', createdAt: 123, read: false },
      { id: 'n2', message: 'Осторожно', type: 'warning', createdAt: 456, read: true },
    ]);
  });

  test('ignores malformed history entries', () => {
    window.localStorage.setItem(NOTIFICATION_HISTORY_STORAGE_KEY, JSON.stringify([
      null,
      { id: 'empty', message: '' },
      { id: 'ok', message: 'Текст', type: 'unknown', createdAt: 10 },
    ]));

    expect(loadNotificationHistory()).toEqual([
      { id: 'ok', message: 'Текст', type: 'info', createdAt: 10, read: false },
    ]);
  });

  test('clear removes storage and broadcasts same-tab cleanup', () => {
    saveNotificationHistory([{ id: 'n1', message: 'Текст', type: 'info', createdAt: 1, read: false }]);
    const listener = jest.fn();
    window.addEventListener(NOTIFICATION_HISTORY_CLEARED_EVENT, listener);

    clearNotificationHistory();

    expect(window.localStorage.getItem(NOTIFICATION_HISTORY_STORAGE_KEY)).toBeNull();
    expect(listener).toHaveBeenCalledTimes(1);
    window.removeEventListener(NOTIFICATION_HISTORY_CLEARED_EVENT, listener);
  });
});
