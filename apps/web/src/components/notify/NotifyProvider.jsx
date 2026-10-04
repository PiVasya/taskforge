import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import {
  AlertTriangle,
  Bell,
  CheckCircle2,
  Info,
  Trash2,
  X,
  XCircle,
} from 'lucide-react';
import {
  clearNotificationHistory,
  loadNotificationHistory,
  NOTIFICATION_HISTORY_CLEARED_EVENT,
  NOTIFICATION_HISTORY_STORAGE_KEY,
  saveNotificationHistory,
} from './notificationHistory';

const NotifyContext = createContext(null);
NotifyContext.displayName = 'NotifyContext';

const TYPE_META = Object.freeze({
  success: { label: 'Успешно', Icon: CheckCircle2 },
  error: { label: 'Ошибка', Icon: XCircle },
  warning: { label: 'Предупреждение', Icon: AlertTriangle },
  info: { label: 'Информация', Icon: Info },
});

function notificationId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function normalizeMessage(message) {
  if (typeof message === 'string') return message.trim();
  if (typeof message === 'number' || typeof message === 'boolean') return String(message);
  return String(message ?? '').trim();
}

function formatNotificationTime(value) {
  const date = new Date(Number(value));
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

export function useNotify() {
  const ctx = useContext(NotifyContext);
  if (!ctx) {
    throw new Error('useNotify must be used within <NotifyProvider>');
  }
  return ctx.notify;
}

export function useNotificationCenter() {
  const ctx = useContext(NotifyContext);
  if (!ctx) {
    throw new Error('useNotificationCenter must be used within <NotifyProvider>');
  }
  return ctx.notificationCenter;
}

export function NotifyProvider({ children }) {
  const [toasts, setToasts] = useState([]);
  const [history, setHistory] = useState(() => loadNotificationHistory());
  const [centerOpen, setCenterOpen] = useState(false);
  const timersRef = useRef(new Map());
  const centerOpenRef = useRef(false);

  useEffect(() => {
    centerOpenRef.current = centerOpen;
  }, [centerOpen]);

  useEffect(() => {
    saveNotificationHistory(history);
  }, [history]);

  const dismissToast = useCallback((id) => {
    const timer = timersRef.current.get(id);
    if (timer) window.clearTimeout(timer);
    timersRef.current.delete(id);
    setToasts((prev) => prev.filter((item) => item.id !== id));
  }, []);

  const clearVisibleToasts = useCallback(() => {
    timersRef.current.forEach((timer) => window.clearTimeout(timer));
    timersRef.current.clear();
    setToasts([]);
  }, []);

  useEffect(() => () => {
    timersRef.current.forEach((timer) => window.clearTimeout(timer));
    timersRef.current.clear();
  }, []);

  useEffect(() => {
    const handleCleared = () => {
      centerOpenRef.current = false;
      clearVisibleToasts();
      setHistory([]);
      setCenterOpen(false);
    };
    const handleStorage = (event) => {
      if (event.key !== NOTIFICATION_HISTORY_STORAGE_KEY) return;
      if (event.newValue === null) {
        centerOpenRef.current = false;
        clearVisibleToasts();
        setCenterOpen(false);
      }
      setHistory(loadNotificationHistory());
    };

    window.addEventListener(NOTIFICATION_HISTORY_CLEARED_EVENT, handleCleared);
    window.addEventListener('storage', handleStorage);
    return () => {
      window.removeEventListener(NOTIFICATION_HISTORY_CLEARED_EVENT, handleCleared);
      window.removeEventListener('storage', handleStorage);
    };
  }, [clearVisibleToasts]);

  const pushNotification = useCallback((message, type = 'info', timeout = 5000) => {
    const normalizedMessage = normalizeMessage(message);
    if (!normalizedMessage) return null;

    const normalizedType = TYPE_META[type] ? type : 'info';
    const id = notificationId();
    const record = {
      id,
      message: normalizedMessage,
      type: normalizedType,
      createdAt: Date.now(),
      read: centerOpenRef.current,
    };

    setHistory((prev) => [record, ...prev]);
    setToasts((prev) => [...prev, record]);

    const duration = Number(timeout);
    const timer = window.setTimeout(() => {
      timersRef.current.delete(id);
      setToasts((prev) => prev.filter((item) => item.id !== id));
    }, Number.isFinite(duration) && duration > 0 ? duration : 5000);
    timersRef.current.set(id, timer);
    return id;
  }, []);

  const markAllRead = useCallback(() => {
    setHistory((prev) => {
      if (!prev.some((item) => !item.read)) return prev;
      return prev.map((item) => (item.read ? item : { ...item, read: true }));
    });
  }, []);

  const openCenter = useCallback(() => {
    centerOpenRef.current = true;
    setCenterOpen(true);
    markAllRead();
  }, [markAllRead]);

  const closeCenter = useCallback(() => {
    centerOpenRef.current = false;
    setCenterOpen(false);
  }, []);

  const clearHistory = useCallback(() => {
    setHistory([]);
    clearNotificationHistory({ broadcast: false });
  }, []);

  useEffect(() => {
    if (!centerOpen) return undefined;
    const onKeyDown = (event) => {
      if (event.key === 'Escape') closeCenter();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [centerOpen, closeCenter]);

  const notify = useMemo(() => {
    const fn = (message, type = 'info', timeout = 5000) => pushNotification(message, type, timeout);
    fn.success = (msg, timeout) => pushNotification(msg, 'success', timeout);
    fn.error = (msg, timeout) => pushNotification(msg, 'error', timeout);
    fn.warn = (msg, timeout) => pushNotification(msg, 'warning', timeout);
    fn.info = (msg, timeout) => pushNotification(msg, 'info', timeout);
    fn.confirm = async ({ title, message } = {}) => {
      const text = `${title ? `${title}\n` : ''}${message || ''}`;
      return window.confirm(text);
    };
    return fn;
  }, [pushNotification]);

  const unreadCount = useMemo(
    () => history.reduce((count, item) => count + (item.read ? 0 : 1), 0),
    [history],
  );

  const notificationCenter = useMemo(() => ({
    history,
    unreadCount,
    open: centerOpen,
    openCenter,
    closeCenter,
    markAllRead,
    clearHistory,
  }), [centerOpen, clearHistory, closeCenter, history, markAllRead, openCenter, unreadCount]);

  const value = useMemo(() => ({ notify, notificationCenter }), [notificationCenter, notify]);

  return (
    <NotifyContext.Provider value={value}>
      {children}

      <div className="tf-toast-stack" aria-live="polite" aria-atomic="false">
        {toasts.map((item) => {
          const meta = TYPE_META[item.type] || TYPE_META.info;
          const Icon = meta.Icon;
          return (
            <div
              key={item.id}
              className={`tf-toast tf-toast--${item.type}`}
              role={item.type === 'error' || item.type === 'warning' ? 'alert' : 'status'}
            >
              <div className="tf-toast__icon" aria-hidden="true">
                <Icon size={19} />
              </div>
              <div className="tf-toast__body">
                <div className="tf-toast__type">{meta.label}</div>
                <div className="tf-toast__message">{item.message}</div>
              </div>
              <button
                type="button"
                className="tf-toast__close"
                onClick={() => dismissToast(item.id)}
                aria-label="Закрыть уведомление"
              >
                <X size={16} />
              </button>
            </div>
          );
        })}
      </div>

      {centerOpen ? (
        <div className="tf-notification-center-overlay" role="presentation" onMouseDown={closeCenter}>
          <section
            className="tf-notification-center"
            role="dialog"
            aria-modal="true"
            aria-labelledby="tf-notification-center-title"
            onMouseDown={(event) => event.stopPropagation()}
          >
            <header className="tf-notification-center__header">
              <div className="tf-notification-center__title-wrap">
                <div className="tf-notification-center__bell" aria-hidden="true"><Bell size={20} /></div>
                <div>
                  <h2 id="tf-notification-center-title">Уведомления</h2>
                  <p>{history.length ? `Сохранено: ${history.length}` : 'История пока пустая'}</p>
                </div>
              </div>
              <div className="tf-notification-center__actions">
                {history.length > 0 ? (
                  <button type="button" className="tf-notification-center__action" onClick={clearHistory} title="Очистить историю">
                    <Trash2 size={18} />
                    <span>Очистить</span>
                  </button>
                ) : null}
                <button type="button" className="tf-notification-center__close" onClick={closeCenter} aria-label="Закрыть уведомления">
                  <X size={19} />
                </button>
              </div>
            </header>

            <div className="tf-notification-center__list">
              {history.length === 0 ? (
                <div className="tf-notification-center__empty">
                  <Bell size={28} />
                  <strong>Новых уведомлений нет</strong>
                  <span>Все сообщения интерфейса будут сохраняться здесь до выхода из аккаунта.</span>
                </div>
              ) : history.map((item) => {
                const meta = TYPE_META[item.type] || TYPE_META.info;
                const Icon = meta.Icon;
                return (
                  <article key={item.id} className={`tf-notification-history-item tf-notification-history-item--${item.type}`}>
                    <div className="tf-notification-history-item__icon" aria-hidden="true"><Icon size={18} /></div>
                    <div className="tf-notification-history-item__body">
                      <div className="tf-notification-history-item__meta">
                        <strong>{meta.label}</strong>
                        <time dateTime={new Date(item.createdAt).toISOString()}>{formatNotificationTime(item.createdAt)}</time>
                      </div>
                      <div className="tf-notification-history-item__message">{item.message}</div>
                    </div>
                  </article>
                );
              })}
            </div>
          </section>
        </div>
      ) : null}
    </NotifyContext.Provider>
  );
}
