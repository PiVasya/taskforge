import React, { useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import Layout from "../components/Layout";
import { Card, Button, Textarea, Select, Field, Badge } from "../components/ui";
import { ArrowLeft } from "lucide-react";
import IfEditor from "../components/IfEditor";

import { getAssignment } from "../api/assignments";
import { runSolutionRich } from "../api/solutions";
import { useNotify } from "../components/notify/NotifyProvider";

import CodeEditor from "../components/CodeEditor";
import CompileErrorPanel from "../components/runner/CompileErrorPanel";
import RuntimeErrorPanel from "../components/runner/RuntimeErrorPanel";
import TestReport from "../components/runner/TestReport";

const LANGS = [
  { v: "cpp", label: "C++" },
  { v: "csharp", label: "C#" },
  { v: "python", label: "Python" },
];

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const notify = useNotify();

  // meta задания
  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");

  // редактор
  const [language, setLanguage] = useState("cpp");
  const [source, setSource] = useState("");
  const [stdin, setStdin] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState(null);

  // ключ черновика ( НЕ подгружаем автоматически )
  const draftKey = useMemo(() => `draft:${assignmentId}:${language}`, [assignmentId, language]);
  const hasDraft = useMemo(() => {
    try { return localStorage.getItem(draftKey) != null; } catch { return false; }
  }, [draftKey]);

  // загрузка задания
  useEffect(() => {
    (async () => {
      try {
        setLoading(true); setErr("");
        const dto = await getAssignment(assignmentId);
        setA(dto);
      } catch (e) {
        setErr(e.message || "Не удалось загрузить задание");
      } finally {
        setLoading(false);
      }
    })();
  }, [assignmentId]);

  // стартовые шаблоны (только если редактор пуст)
  useEffect(() => {
    if (source.trim()) return;
    if (language === "python") {
      setSource("# ваш код здесь\n");
    } else if (language === "cpp") {
      setSource(`#include <iostream>
using namespace std;
int main(){ /* ... */ return 0; }`);
    } else if (language === "csharp") {
      setSource(`using System;
class Program { static void Main(){ /* ... */ } }`);
    }
  }, [language, source]);

  const saveDraft = () => {
    try {
      localStorage.setItem(draftKey, source || "");
      notify.success("Черновик сохранён");
    } catch { notify.warn("Не удалось сохранить черновик"); }
  };
  const loadDraft = () => {
    try {
      const s = localStorage.getItem(draftKey);
      if (s != null) { setSource(s); notify.success("Черновик загружен"); }
      else notify.info("Черновик отсутствует");
    } catch { notify.warn("Не удалось загрузить черновик"); }
  };
  const clearDraft = () => {
    try { localStorage.removeItem(draftKey); notify.success("Черновик очищен"); } catch {}
  };

  const run = async () => {
    setBusy(true);
    setResult(null);
    try {
      const res = await runSolutionRich({
        assignmentId,
        language,
        source,
        stdin,
        timeLimitMs: 2000,
        memoryLimitMb: 256,
      });
      setResult(res);
      // мягкие уведомления по статусу
      if      (res.status === "passed") notify.success("Все тесты пройдены");
      else if (res.status === "failed_tests") notify.info("Есть непройденные тесты");
      else if (res.status === "compile_error") notify.warn("Ошибка компиляции");
      else if (res.status === "runtime_error") notify.warn("Исключение во время выполнения");
      else if (res.status === "infrastructure_error") notify.error(res.message || "Ошибка инфраструктуры");
    } catch (e) {
      notify.error(e?.message || "Не удалось выполнить код");
    } finally { setBusy(false); }
  };

  if (loading) return <Layout><div className="text-slate-500">Загрузка…</div></Layout>;
  if (!a) return <Layout><div className="text-red-500">{err || "Задание не найдено"}</div></Layout>;

  const publicTests = (a.testCases || []).filter(t => !t.isHidden);

  return (
    <Layout>
      <div className="flex items-center justify-between mb-6">
        <Link to={`/course/${a.courseId}`} className="text-brand-600 hover:underline">
          <ArrowLeft size={16} /> к заданиям курса
        </Link>
        <IfEditor>
          <Link to={`/assignment/${a.id}/edit`} className="btn-outline">Редактировать</Link>
        </IfEditor>
      </div>

      <div className="grid lg:grid-cols-3 gap-6">
        {/* ====== Левая колонка: условие + публичные тесты ====== */}
        <div className="lg:col-span-2 space-y-5">
          <Card>
            <h1 className="text-2xl font-semibold mb-1">{a.title}</h1>
            {a.tags && (
              <div className="flex flex-wrap gap-2 mb-3">
                {a.tags.split(",").filter(Boolean).map((t) => (
                  <Badge key={t.trim()}>{t.trim()}</Badge>
                ))}
              </div>
            )}
            <div className="prose prose-slate dark:prose-invert max-w-none">
              {a.description ? (
                <div dangerouslySetInnerHTML={{ __html: (a.description || "").replace(/\n/g, "<br/>") }} />
              ) : (
                <p className="text-slate-500">Описание не задано.</p>
              )}
            </div>
          </Card>

          <Card>
            <h2 className="text-lg font-semibold mb-3">Публичные тесты</h2>
            {publicTests.length === 0 ? (
              <div className="text-slate-500">Публичных тестов нет.</div>
            ) : (
              <div className="grid sm:grid-cols-2 gap-4">
                {publicTests.map((t, i) => (
                  <div key={t.id ?? i} className="rounded-xl border border-slate-200 dark:border-slate-800 p-3 bg-white/60 dark:bg-slate-900/40">
                    <div className="text-xs text-slate-500 mb-1">Input</div>
                    <pre className="whitespace-pre-wrap text-sm">{t.input}</pre>
                    <div className="text-xs text-slate-500 mt-2 mb-1">Expected Output</div>
                    <pre className="whitespace-pre-wrap text-sm">{t.expectedOutput}</pre>
                  </div>
                ))}
              </div>
            )}
          </Card>
        </div>

        {/* ====== Правая колонка: редактор + запуск + результаты ====== */}
        <div className="space-y-4">
          <Card>
            <div className="grid gap-3">
              <Field label="Язык">
                <Select value={language} onChange={(e) => setLanguage(e.target.value)}>
                  {LANGS.map((l) => <option key={l.v} value={l.v}>{l.label}</option>)}
                </Select>
              </Field>

              <Field label="Ваш код">
                <CodeEditor
                  language={language}
                  value={source}
                  onChange={setSource}
                  placeholder="// Напишите решение…"
                />
              </Field>

              <details className="mt-2">
                <summary className="cursor-pointer select-none text-sm text-slate-500">
                  stdin (опционально)
                </summary>
                <Textarea rows={4} value={stdin} onChange={(e) => setStdin(e.target.value)} className="mt-2" />
              </details>

              <div className="flex flex-wrap gap-2 pt-2">
                <Button onClick={run} disabled={busy || !source.trim()}>
                  {busy ? "Выполняю…" : "Запустить"}
                </Button>
                <Button variant="outline" onClick={saveDraft}>Сохранить черновик</Button>
                <Button variant="ghost" onClick={loadDraft} disabled={!hasDraft}>Загрузить черновик</Button>
                <Button variant="ghost" onClick={clearDraft} disabled={!hasDraft}>Очистить черновик</Button>
              </div>
            </div>
          </Card>

          {result && (
            <>
              {result.compile && <CompileErrorPanel compile={result.compile} source={source} />}
              {result.run && <RuntimeErrorPanel run={result.run} source={source} />}
              <TestReport tests={result.tests} />
            </>
          )}
        </div>
      </div>
    </Layout>
  );
}
