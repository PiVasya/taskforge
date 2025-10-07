import React from "react";
import { CheckCircle2, XCircle } from "lucide-react";

export default function TestReport({ tests }) {
  if (!Array.isArray(tests) || tests.length === 0) return null;
  return (
    <div className="rounded-2xl border border-slate-200 dark:border-slate-700 p-4">
      <div className="font-semibold mb-2">Результаты тестов</div>
      <div className="space-y-3">
        {tests.map((t, idx) => (
          <div key={idx} className={`rounded-xl p-3 border ${t.passed ? "border-emerald-300 bg-emerald-50 dark:bg-emerald-900/20" : "border-red-300 bg-red-50 dark:bg-red-900/20"}`}>
            <div className="flex items-center gap-2">
              {t.passed ? <CheckCircle2 size={18} /> : <XCircle size={18} />}
              <div className="font-medium">{t.name || `Тест ${idx + 1}`}</div>
              <div className="ml-auto text-xs opacity-70">{t.timeMs} ms</div>
            </div>
            {!t.passed && (
              <div className="mt-2 grid md:grid-cols-3 gap-3 text-sm">
                <div>
                  <div className="text-slate-500">Ожидалось</div>
                  <pre className="mt-1 bg-white/60 dark:bg-slate-900/30 rounded p-2 whitespace-pre-wrap">{t.expected}</pre>
                </div>
                <div>
                  <div className="text-slate-500">Получено</div>
                  <pre className="mt-1 bg-white/60 dark:bg-slate-900/30 rounded p-2 whitespace-pre-wrap">{t.actual}</pre>
                </div>
                <div>
                  <div className="text-slate-500">Ввод</div>
                  <pre className="mt-1 bg-white/60 dark:bg-slate-900/30 rounded p-2 whitespace-pre-wrap">{t.input}</pre>
                </div>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
