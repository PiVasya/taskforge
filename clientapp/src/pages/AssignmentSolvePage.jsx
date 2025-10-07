import React, { useEffect, useMemo, useState } from "react";
import Layout from "../components/Layout";
import { Card, Button, Textarea, Select, Field, Input } from "../components/ui";
import { useParams } from "react-router-dom";
import { runSolutionRich } from "../api/solutions";
import { useNotify } from "../components/notify/NotifyProvider";
import CompileErrorPanel from "../components/runner/CompileErrorPanel";
import RuntimeErrorPanel from "../components/runner/RuntimeErrorPanel";
import TestReport from "../components/runner/TestReport";
import { getAssignment } from "../api/assignments";
import CodeEditor from "../components/CodeEditor";


const LANGS = [
  { v: "csharp", label: "C#" },
  { v: "cpp", label: "C++" },
  { v: "python", label: "Python" },
];

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const notify = useNotify();

  const [meta, setMeta] = useState(null);
  const [language, setLanguage] = useState("csharp");
  const [source, setSource] = useState("");
  const [stdin, setStdin] = useState("");
  const [result, setResult] = useState(null);
  const [busy, setBusy] = useState(false);

  const draftKey = useMemo(() => `draft:${assignmentId}:${language}`, [assignmentId, language]);

  useEffect(() => {
    (async () => {
      try {
        const a = await getAssignment(assignmentId);
        setMeta(a);
        // при желании можно подставлять стартовый шаблон по языку
      } catch (e) {
        notify.error(e?.message || "Не удалось загрузить задание");
      }
    })();
  }, [assignmentId, notify]);

  const hasDraft = (() => {
    try { return localStorage.getItem(draftKey) != null; } catch { return false; }
  })();

  const saveDraft = () => {
    try {
      localStorage.setItem(draftKey, source || "");
      notify.success("Черновик сохранён");
    } catch {
      notify.warn("Не удалось сохранить черновик");
    }
  };

  const loadDraft = () => {
    try {
      const saved = localStorage.getItem(draftKey);
      if (saved != null) {
        setSource(saved);
        notify.success("Черновик загружен");
      } else {
        notify.info("Черновик отсутствует");
      }
    } catch {
      notify.warn("Не удалось загрузить черновик");
    }
  };

  const clearDraft = () => {
    try {
      localStorage.removeItem(draftKey);
      notify.success("Черновик очищен");
    } catch { /* no-op */ }
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

      if (res.status === "infrastructure_error") {
        notify.error(res.message || "Ошибка инфраструктуры раннера");
      } else if (res.status === "compile_error") {
        notify.warn("Ошибка компиляции");
      } else if (res.status === "runtime_error") {
        notify.warn("Исключение во время выполнения");
      } else if (res.status === "failed_tests") {
        notify.info("Есть непройденные тесты");
      } else if (res.status === "passed") {
        notify.success("Все тесты пройдены");
      }
    } catch (e) {
      notify.error(e?.message || "Не удалось выполнить код");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Layout>
      <div className="mb-4">
        <h1 className="text-2xl font-semibold">Решение задания</h1>
        {meta && (
          <div className="text-sm text-slate-500 mt-1">
            {meta.title} · сложность {meta.difficulty ?? "-"}
          </div>
        )}
      </div>

      <Card className="mb-4">
        <div className="grid sm:grid-cols-4 gap-3">
          <Field label="Язык">
            <Select value={language} onChange={(e) => setLanguage(e.target.value)}>
              {LANGS.map((l) => (
                <option key={l.v} value={l.v}>{l.label}</option>
              ))}
            </Select>
          </Field>
          <div className="sm:col-span-3 flex items-end gap-2">
            <Button onClick={run} disabled={busy}>
              {busy ? "Выполняю…" : "Запустить"}
            </Button>
            <Button variant="outline" onClick={saveDraft}>Сохранить черновик</Button>
            <Button variant="ghost" onClick={loadDraft} disabled={!hasDraft}>Загрузить черновик</Button>
            <Button variant="ghost" onClick={clearDraft} disabled={!hasDraft}>Очистить черновик</Button>
          </div>
        </div>

        <Field label="Код" className="mt-4">
          <CodeEditor
            language={language}
            value={source}
            onChange={setSource}
            placeholder="// Напишите код здесь…"
          />
        </Field>


        <details className="mt-3">
          <summary className="cursor-pointer select-none text-sm text-slate-500">Пользовательский ввод (stdin)</summary>
          <Textarea rows={4} value={stdin} onChange={(e) => setStdin(e.target.value)} placeholder="Ввод программы" />
        </details>
      </Card>

      {result && (
        <div className="space-y-4">
          {result.compile && <CompileErrorPanel compile={result.compile} source={source} />}
          {result.run && <RuntimeErrorPanel run={result.run} source={source} />}
          <TestReport tests={result.tests} />
        </div>
      )}
    </Layout>
  );
}
