import React, { createContext, useCallback, useContext, useMemo, useRef, useState } from "react";
import { CheckCircle2, AlertTriangle, Info, XCircle, X } from "lucide-react";

const NotifyCtx = createContext(null);

const ICONS = {
  success: CheckCircle2,
  error: XCircle,
  info: Info,
  warn: AlertTriangle,
};

let _id = 0;
const nextId = () => (++_id).toString();

export function NotifyProvider({ children }) {
  const [toasts, setToasts] = useState([]);
  const [confirmState, setConfirmState] = useState(null); // {title,message,okText,cancelText,resolve}

  const remove = useCallback((id) => {
    setToasts((xs) => xs.filter((t) => t.id !== id));
  }, []);

  const push = useCallback((toast) => {
    const id = nextId();
    const t = {
      id,
      tone: toast.tone || "info",          // success | error | info | warn
      title: toast.title || "",
      message: toast.message || "",
      icon: toast.icon,                    // кастомный React-элемент (не обяз.)
      timeout: toast.timeout ?? 3200,      // ms, null/0 = не закрывать авто
      actions: toast.actions || null,      // [{text, onClick}]
    };
    setToasts((xs) => [...xs, t]);
    if (t.timeout && t.timeout > 0) {
      setTimeout(() => remove(id), t.timeout);
    }
    return id;
  }, [remove]);

  const api = useMemo(() => {
    const mk = (tone) => (message, opts={}) => push({ ...opts, tone, message, title: opts.title ?? ({
      success: "Успех",
      error: "Ошибка",
      info: "Инфо",
      warn: "Внимание",
    }[tone]) });
    return {
      notify: push,
      success: mk("success"),
      error: mk("error"),
      info: mk("info"),
      warn: mk("warn"),
      custom: (opts) => push(opts),
      confirm: ({ title="Подтверждение", message="", okText="Да", cancelText="Отмена" } = {}) =>
        new Promise((resolve) => {
          setConfirmState({ title, message, okText, cancelText, resolve });
        }),
    };
  }, [push]);

  const onConfirmOk = () => {
    const res = confirmState?.resolve;
    setConfirmState(null);
    if (res) res(true);
  };
  const onConfirmCancel = () => {
    const res = confirmState?.resolve;
    setConfirmState(null);
    if (res) res(false);
  };

  return (
    <NotifyCtx.Provider value={api}>
      {children}

      {/* Toasts (верх-право) */}
      <div className="fixed top-4 right-4 z-[9999] flex flex-col gap-3 max-w-sm">
        {toasts.map((t) => {
          const ToneIcon = t.icon || ICONS[t.tone] || Info;
          const toneStyles = {
            success: "border-emerald-300 bg-emerald-50 text-emerald-800 dark:bg-emerald-950/30 dark:text-emerald-200 dark:border-emerald-700",
            error:   "border-red-300 bg-red-50 text-red-800 dark:bg-red-950/30 dark:text-red-200 dark:border-red-700",
            info:    "border-sky-300 bg-sky-50 text-sky-800 dark:bg-sky-950/30 dark:text-sky-200 dark:border-sky-700",
            warn:    "border-amber-300 bg-amber-50 text-amber-900 dark:bg-amber-950/30 dark:text-amber-100 dark:border-amber-700",
          }[t.tone];

          return (
            <div key={t.id}
                 className={`rounded-2xl border shadow-sm px-4 py-3 backdrop-blur-sm ${toneStyles}`}>
              <div className="flex items-start gap-3">
                <ToneIcon className="mt-0.5 shrink-0" size={20} />
                <div className="min-w-0">
                  {t.title ? <div className="font-semibold leading-5">{t.title}</div> : null}
                  {t.message ? <div className="text-sm leading-5 break-words">{t.message}</div> : null}

                  {Array.isArray(t.actions) && t.actions.length > 0 && (
                    <div className="mt-3 flex gap-2">
                      {t.actions.map((a, i) => (
                        <button
                          key={i}
                          onClick={() => { a.onClick?.(); remove(t.id); }}
                          className="text-sm px-2.5 py-1 rounded-xl border border-current/30 hover:bg-white/40 dark:hover:bg-white/10"
                        >
                          {a.text}
                        </button>
                      ))}
                    </div>
                  )}
                </div>

                <button
                  className="ml-auto opacity-70 hover:opacity-100"
                  onClick={() => remove(t.id)}
                  title="Закрыть"
                >
                  <X size={18} />
                </button>
              </div>
            </div>
          );
        })}
      </div>

      {/* Confirm Modal */}
      {confirmState && (
        <div className="fixed inset-0 z-[10000] flex items-center justify-center">
          <div className="absolute inset-0 bg-black/50" onClick={onConfirmCancel} />
          <div className="relative bg-white dark:bg-slate-900 rounded-2xl shadow-2xl max-w-lg w-[92%] p-6 border border-slate-200 dark:border-slate-700">
            <div className="text-lg font-semibold">{confirmState.title}</div>
            {confirmState.message && (
              <div className="mt-2 text-slate-600 dark:text-slate-300 whitespace-pre-wrap">
                {confirmState.message}
              </div>
            )}
            <div className="mt-5 flex justify-end gap-2">
              <button
                onClick={onConfirmCancel}
                className="px-4 py-2 rounded-xl border border-slate-300 dark:border-slate-600 hover:bg-slate-50 dark:hover:bg-slate-800"
              >
                {confirmState.cancelText || "Отмена"}
              </button>
              <button
                onClick={onConfirmOk}
                className="px-4 py-2 rounded-xl bg-sky-600 text-white hover:bg-sky-700"
              >
                {confirmState.okText || "ОК"}
              </button>
            </div>
          </div>
        </div>
      )}
    </NotifyCtx.Provider>
  );
}

export function useNotify() {
  const ctx = useContext(NotifyCtx);
  if (!ctx) throw new Error("useNotify must be used within <NotifyProvider>");
  return ctx;
}
