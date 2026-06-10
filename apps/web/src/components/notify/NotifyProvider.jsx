import React, { createContext, useCallback, useContext, useMemo, useState } from 'react';

const NotifyContext = createContext(null);

export function useNotify() {
  const ctx = useContext(NotifyContext);
  if (!ctx) {
    throw new Error('useNotify must be used within <NotifyProvider>');
  }
  return ctx.notify;
}

export function NotifyProvider({ children }) {
  const [notifications, setNotifications] = useState([]);

  const pushNotification = useCallback((message, type = 'info', timeout = 5000) => {
    const id = Date.now() + Math.random();
    setNotifications((prev) => [...prev, { id, message, type }]);
    setTimeout(() => {
      setNotifications((prev) => prev.filter((n) => n.id !== id));
    }, timeout);
  }, []);

  const notify = useMemo(() => {
    const fn = (message, type = 'info', timeout = 5000) => pushNotification(message, type, timeout);
    fn.success = (msg, timeout) => pushNotification(msg, 'success', timeout);
    fn.error = (msg, timeout) => pushNotification(msg, 'error', timeout);
    fn.warn = (msg, timeout) => pushNotification(msg, 'warning', timeout);
    fn.info = (msg, timeout) => pushNotification(msg, 'info', timeout);
    fn.confirm = async ({ title, message } = {}) => {
      const text = `${title ? title + '\n' : ''}${message || ''}`;
      return window.confirm(text);
    };
    return fn;
  }, [pushNotification]);

  const value = useMemo(() => ({ notify }), [notify]);

  return (
    <NotifyContext.Provider value={value}>
      {children}

      <div className="fixed top-4 right-4 z-50 space-y-3 max-w-sm">
        {notifications.map((n) => (
          <div
            key={n.id}
            className={`rounded-xl p-4 shadow-soft border-l-4
              ${
                n.type === 'error'
                  ? 'border-red-500 bg-red-50 text-red-800 dark:bg-red-900/70 dark:text-red-200'
                  : n.type === 'success'
                    ? 'border-emerald-500 bg-emerald-50 text-emerald-800 dark:bg-emerald-900/70 dark:text-emerald-200'
                    : n.type === 'warning'
                      ? 'border-yellow-500 bg-yellow-50 text-yellow-800 dark:text-yellow-200 dark:bg-yellow-900/70'
                      : 'border-pink-500 bg-pink-50 text-pink-800 dark:bg-pink-900/70 dark:text-pink-200'
              }`}
          >
            {n.message}
          </div>
        ))}
      </div>
    </NotifyContext.Provider>
  );
}
