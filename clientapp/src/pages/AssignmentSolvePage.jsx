import React, { useEffect, useState } from "react";
import Layout from "../components/Layout";
import { Card, Button, Textarea, Select } from "../components/ui";
import { useParams } from "react-router-dom";
import { runSolutionRich } from "../api/solutions";
import { useNotify } from "../components/notify/NotifyProvider";
import CompileErrorPanel from "../components/runner/CompileErrorPanel";
import RuntimeErrorPanel from "../components/runner/RuntimeErrorPanel";
import TestReport from "../components/runner/TestReport";
import { getAssignment } from "../api/assignments"; // чтобы подтянуть инфо по заданию

const LANGS = [
  { v: "csharp", label: "C#" },
  { v: "cpp", label: "C++" },
  { v: "python", label: "Python" },
];

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const notify = useNotify();

  const [assignment, setAssignment] = useState(null);

  const [language, setLanguage] = useState("csharp");
  const [source, setSource] = useState("");
  const [stdin, setStdin] = useState("");

  const [result, setResult] = useState(null);
  const [busy, setBusy] = useState(false);

  const draftKey = `draft:${assignmentId}:${language}`;

  useEffect(() => {
    (async () => {
      try {
        const a = await getAssignment(assignmentId);
        setAssignment(a);
      } catch {}
    })();
  }, [assignmentId]);

  // загрузка черновика при смене языка
  useEffect(() => {
    try {
      const saved = localStorage.getItem(draftKey);
      if (saved != null) setSource(saved);
    } catch {}
  }, [draftKey]);

  const saveDraft = () => {
    try {
      localStorage.setItem(draftKey, source || "");
      notify.success("Черновик сохранён");
    } catch {
      notify.warn("Не удалось сохранить черновик");
    }
  };

  const onRun = async () => {
    setBusy(true);
    setResult(null);
    try {
      const res = await runSolutionRich({
        assignmentId, language, source, stdin,
      });
      setResult(res);

      if (res.status === "passed") notify.success("Все тесты пройдены 🎉");
      else if (res.status === "failed_tests") notify.info("Есть непройденные тесты");
      else if (res.status === "compile_error") notify.warn("Ошибка компиляции");
      else if (res.status === "runtime_error") notify.warn("Исключение во время выполнения");
      else if (res.status === "infrastructure_error") notify.error(res.message || "Ошибка инфраструктуры");
    } catch (e) {
      notify.error(e.userMessage || e.message || "Не удалось выполнить код");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Layout>
      <Card className="mb-4 p-4 space-y-3">
        <div className="flex items-center gap-3">
          <Select value={language} onChange={(e) => setLanguage(e.target.value)}>
            {LANGS.map((l) => (
              <option key={l.v} value={l.v}>{l.label}</option>
            ))}
          </Select>
          <Button onClick={onRun} disabled={busy}>
            {busy ? "Запуск…" : "Запустить"}
          </Button>
          <Button variant="outline" onClick={saveDraft}>Сохранить черновик</Button>
        </div>

        <Textarea rows={16} value={source} onChange={(e) => setSource(e.target.value)} placeholder="// ваш код здесь" />

        <details className="mt-2">
          <summary className="cursor-pointer text-sm text-slate-500">stdin (опционально)</summary>
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
