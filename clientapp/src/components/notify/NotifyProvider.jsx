import React, { createContext, useContext, useState } from 'react';

/**
 * Контекст уведомлений. Позволяет компонентам вызывать notify(message, type),
 * чтобы показать временное всплывающее уведомление пользователю.
 *
 * type: 'info' | 'success' | 'warning' | 'error'; timeout по умолчанию 5000 мс.
 */

const NotifyContext = createContext(null);

/**
 * Хук для доступа к notify в компонентах.
 * Выдаёт ошибку, если вызван вне NotifyProvider.
 */
export function useNotify() {
  const ctx = useContext(NotifyContext);
  if (!ctx) {
    throw new Error('useNotify must be used within <NotifyProvider>');
  }
  return ctx.notify;
}

/**
 * Провайдер, который хранит список уведомлений в локальном состоянии
 * и автоматически удаляет их по тайм‑ауту.
 */
export function NotifyProvider({ children }) {
  const [notifications, setNotifications] = useState([]);

  // Базовая функция: показывает уведомление нужного типа и удаляет его по тайм‑ауту.
  const notify = (message, type = 'info', timeout = 5000) => {
    const id = Date.now() + Math.random();
    setNotifications((prev) => [...prev, { id, message, type }]);
    setTimeout(() => {
      setNotifications((prev) => prev.filter((n) => n.id !== id));
    }, timeout);
  };

  // Добавляем синтаксический сахар: notify.success('…'), notify.error('…'), ...
  notify.success = (msg, timeout) => notify(msg, 'success', timeout);
  notify.error   = (msg, timeout) => notify(msg, 'error', timeout);
  notify.warn    = (msg, timeout) => notify(msg, 'warning', timeout);
  notify.info    = (msg, timeout) => notify(msg, 'info', timeout);

  /**
   * Показывает диалог подтверждения. Возвращает Promise<boolean>.
   * Здесь используется простой window.confirm, но при желании можно
   * заменить на собственный модальный компонент.
   */
  notify.confirm = async ({
    title,
    message,
    okText = 'OK',
    cancelText = 'Cancel',
  } = {}) => {
    return new Promise((resolve) => {
      const text = `${title ? title + '\n' : ''}${message || ''}`;
      const result = window.confirm(text);
      resolve(result);
    });
  };

  return (
    <NotifyContext.Provider value={{ notify }}>
      {children}
      {/* отрисовываем уведомления в правом верхнем углу */}
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
                  ? 'border-yellow-500 bg-yellow-50 text-yellow-800 dark:bg-yellow-900/70 dark:text-yellow-200'
                  : 'border-blue-500 bg-blue-50 text-blue-800 dark:bg-blue-900/70 dark:text-blue-200'
              }`}
          >
            {n.message}
          </div>
        ))}
      </div>
    </NotifyContext.Provider>
  );
}
