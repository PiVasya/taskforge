import React, { useEffect, useMemo, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";

import Layout from "../components/Layout";
import { useNotify } from "../components/notify/NotifyProvider";
import { extractApiErrorMessages, handleApiError } from "../utils/handleApiError";
import { notifyOnce } from "../utils/notifyOnce";

import { getAssignment, updateAssignment, deleteAssignment } from "../api/assignments";
import { getTaskTestEdit, saveTaskTestEdit } from "../api/taskTests";
import { getMathTaskEdit, saveMathTaskEdit } from "../api/mathTasks";

import { Card, Button, Field, Input, Textarea, Select } from "../components/ui";
import { Save, Trash2, ArrowLeft, PlusCircle, Bot } from "lucide-react";
import TaskTestEditor from "./TaskTestEditor";
import MathTaskEditor from "./MathTaskEditor";
import StatementEditor from "../components/tiptap/StatementEditor";
import { uploadImageTestReference } from "../api/imageTests";
import { useRoleFlags } from "../contexts/EditorModeContext";




const LANGS_BY_TYPE = {
  "code-test": [
    { value: "cpp", label: "C++" },
    { value: "csharp", label: "C#" },
    { value: "javascript", label: "JavaScript" },
    { value: "pascal", label: "Pascal" },
    { value: "java", label: "Java" },
  ],
  
  "image-test": [
    { value: "pascal", label: "Pascal" },
    { value: "cpp", label: "C++" },
  ],
};

export default function AssignmentEditPage() {
  const { assignmentId } = useParams();
  const nav = useNavigate();
  const notify = useNotify();
  const { isAdmin } = useRoleFlags();

  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState("");
  const [saveIssues, setSaveIssues] = useState([]);

  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [type, setType] = useState("code-test");
  const [tags, setTags] = useState("");
  const [difficulty, setDifficulty] = useState(1);
  const [rating, setRating] = useState(1);
  const [isHidden, setIsHidden] = useState(false);
  const [isAiDraft, setIsAiDraft] = useState(false);
  const [lifecycleStatus, setLifecycleStatus] = useState("published");
  const [testCases, setTestCases] = useState([]);

  
  const [codeForbiddenCallsText, setCodeForbiddenCallsText] = useState("");
  const [codeRequiredCallsText, setCodeRequiredCallsText] = useState("");

  const [testSettings, setTestSettings] = useState({
    shuffleQuestions: true,
    shuffleAnswers: true,
    maxAttempts: 1,
    passPercent: 60,
    allowReview: true,
    attemptTimeLimitsSeconds: [],
  });
  const [testQuestions, setTestQuestions] = useState([]);


  const [mathSettings, setMathSettings] = useState({
    maxAttempts: 1,
    passPercent: 60,
    shuffleBlocks: false,
    allowReview: true,
    attemptTimeLimitsSeconds: [],
  });
  const [mathBlocks, setMathBlocks] = useState([]);

  
  const [imageTestReferenceKey, setImageTestReferenceKey] = useState("");
  const [imageTestThreshold, setImageTestThreshold] = useState(90);

  
  const [allowedLanguages, setAllowedLanguages] = useState([]);
  const [langToAdd, setLangToAdd] = useState("");

  const [courseId, setCourseId] = useState(null);

  useEffect(() => {
    (async () => {
      try {

        setLoading(true);
        setErr("");
        const a = await getAssignment(assignmentId); 

        if (!a?.canEdit) {
          notifyOnce("no-edit-assignment", () =>
            notify.warn("Нельзя редактировать данное задание")
          );
          nav(`/assignment/${assignmentId}`, { replace: true });
          return;
        }

        
        setCourseId(a.courseId || null);
        setTitle(a.title || "");
        setDescription(a.description || "");
        setType(a.type || "code-test");
        setAllowedLanguages(Array.isArray(a.allowedLanguages) ? a.allowedLanguages : []);
        setTags(a.tags || "");
        setDifficulty(Number(a.difficulty || 1));
        setRating(typeof a.rating === "number" ? a.rating : Number(a.rating || 1));
        setIsHidden(!!a.isHidden);
        setIsAiDraft(!!a.isAiDraft);
        setLifecycleStatus(a.lifecycleStatus || (a.isHidden ? "draft" : "published"));

        
        const forb = Array.isArray(a.codeForbiddenCalls) ? a.codeForbiddenCalls : [];
        const reqd = Array.isArray(a.codeRequiredCalls) ? a.codeRequiredCalls : [];
        setCodeForbiddenCallsText(forb.join("\n"));
        setCodeRequiredCallsText(reqd.join("\n"));
        setImageTestReferenceKey(a.imageTestReferenceKey || "");
        setImageTestThreshold(
          typeof a.imageTestSimilarityThreshold === "number"
            ? a.imageTestSimilarityThreshold
            : 90
        );
        setTestCases(
          Array.isArray(a.testCases) && a.testCases.length
            ? a.testCases.map((t) => ({
                input: t.input ?? "",
                expectedOutput: t.expectedOutput ?? "",
                isHidden: !!t.isHidden,
              }))
            : [{ input: "", expectedOutput: "", isHidden: false }]
        );

        
        if ((a.type || "").trim() === "test") {
          try {
            const te = await getTaskTestEdit(assignmentId);
            setTestSettings(te.settings || {
              shuffleQuestions: true,
              shuffleAnswers: true,
              maxAttempts: 1,
              passPercent: 60,
              allowReview: true,
              attemptTimeLimitsSeconds: [],
            });
            setTestQuestions(Array.isArray(te.questions) ? te.questions : []);
          } catch (e2) {
            
          }
        }

        if ((a.type || "").trim() === "math") {
          try {
            const me = await getMathTaskEdit(assignmentId);
            setMathSettings(me.settings || {
              maxAttempts: 1,
              passPercent: 60,
              shuffleBlocks: false,
              allowReview: true,
              attemptTimeLimitsSeconds: [],
            });
            setMathBlocks(Array.isArray(me.blocks) ? me.blocks : []);
          } catch (e2) {
            
          }
        }
      } catch (e) {
        handleApiError(e, notify, "Ошибка загрузки задания");
      } finally {
        setLoading(false);
      }
    })();
  
  }, [assignmentId, nav]); 


  
  useEffect(() => {
    const opts = LANGS_BY_TYPE[type] || null;
    if (!opts) {
      
      setAllowedLanguages([]);
      setLangToAdd("");
      return;
    }
    const allowedSet = new Set(opts.map((x) => x.value));
    setAllowedLanguages((prev) => (Array.isArray(prev) ? prev.filter((x) => allowedSet.has(x)) : []));
    setLangToAdd("");
  }, [type]);

  const validationIssues = useMemo(() => {
    const issues = [];
    const normalizedType = (type || '').trim();
    const normalizedTitle = (title || '').trim();
    const plainDescription = String(description || '').replace(/<[^>]*>/g, ' ').replace(/&nbsp;/g, ' ').trim();

    if (!normalizedTitle) issues.push('Укажи название задания.');
    if (normalizedTitle.length > 200) issues.push('Название не должно быть длиннее 200 символов.');
    if (!plainDescription) issues.push('Заполни условие задания.');
    if (!['code-test', 'image-test', 'test', 'math'].includes(normalizedType)) issues.push('Выбран неподдерживаемый тип задания.');
    if (![1, 2, 3].includes(Number(difficulty))) issues.push('Сложность должна быть 1, 2 или 3.');
    if (!Number.isFinite(Number(rating)) || Number(rating) < 0) issues.push('Рейтинг должен быть целым числом не меньше 0.');

    if (normalizedType === 'code-test') {
      if (!Array.isArray(testCases) || testCases.length === 0) {
        issues.push('Для code-test нужен хотя бы один тест-кейс.');
      } else {
      }
    }

    if (normalizedType === 'image-test') {
      if (!String(imageTestReferenceKey || '').trim()) issues.push('Для image-test нужно загрузить эталонную картинку.');
      const threshold = Number(imageTestThreshold);
      if (!Number.isFinite(threshold) || threshold < 0 || threshold > 100) {
        issues.push('Порог совпадения для image-test должен быть от 0 до 100.');
      }
    }

    if (normalizedType === 'test') {
      if (!Array.isArray(testQuestions) || testQuestions.length === 0) {
        issues.push('Добавь хотя бы один вопрос в тест.');
      } else {
        testQuestions.forEach((q, idx) => {
          const prompt = String(q?.prompt ?? '').trim();
          const qType = String(q?.type || 'single-choice');
          if (!prompt) issues.push(`Вопрос #${idx + 1}: заполни текст вопроса.`);

          if (qType === 'single-choice' || qType === 'multi-choice') {
            const opts = Array.isArray(q?.options) ? q.options : [];
            const nonEmpty = opts.filter((o) => String(o?.text ?? '').trim());
            const correct = Array.isArray(q?.correctOptionKeys) ? q.correctOptionKeys.filter(Boolean) : [];
            if (nonEmpty.length < 2) issues.push(`Вопрос #${idx + 1}: нужно минимум два варианта ответа.`);
            if (correct.length === 0) issues.push(`Вопрос #${idx + 1}: отметь хотя бы один правильный ответ.`);
          }

          if (qType === 'fill' || qType === 'text') {
            const answers = Array.isArray(q?.acceptedAnswers)
              ? q.acceptedAnswers.map((x) => String(x || '').trim()).filter(Boolean)
              : [];
            if (answers.length === 0) issues.push(`Вопрос #${idx + 1}: добавь хотя бы один допустимый ответ.`);
          }
        });
      }

      if (!Number.isFinite(Number(testSettings?.maxAttempts)) || Number(testSettings?.maxAttempts) < 1) {
        issues.push('У теста количество попыток должно быть не меньше 1.');
      }
      const passPercent = Number(testSettings?.passPercent);
      if (!Number.isFinite(passPercent) || passPercent < 0 || passPercent > 100) {
        issues.push('Проходной процент должен быть от 0 до 100.');
      }
    }

    if (normalizedType === 'math') {
      if (!Array.isArray(mathBlocks) || mathBlocks.length === 0) {
        issues.push('Добавь хотя бы один блок в math-задание.');
      } else {
        mathBlocks.forEach((b, idx) => {
          const prompt = String(b?.prompt ?? '').trim();
          const rich = String(b?.promptContentJson ?? '').trim();
          const kind = String(b?.kind || 'info');
          if (!prompt && !rich) issues.push(`Math-блок #${idx + 1}: заполни текст блока.`);
          if (['single-choice', 'multi-choice'].includes(kind)) {
            const opts = Array.isArray(b?.options) ? b.options.filter((o) => String(o?.text ?? '').trim()) : [];
            const correct = Array.isArray(b?.correctOptionKeys) ? b.correctOptionKeys.filter(Boolean) : [];
            if (opts.length < 2) issues.push(`Math-блок #${idx + 1}: минимум два варианта.`);
            if (correct.length === 0) issues.push(`Math-блок #${idx + 1}: отметь правильные варианты.`);
          }
          if (['number', 'expression', 'set'].includes(kind)) {
            const answers = Array.isArray(b?.acceptedAnswers) ? b.acceptedAnswers.map((x) => String(x || '').trim()).filter(Boolean) : [];
            if (answers.length === 0) issues.push(`Math-блок #${idx + 1}: добавь допустимые ответы.`);
          }
          if (kind === 'order') {
            const items = Array.isArray(b?.orderItems) ? b.orderItems.map((x) => String(x || '').trim()).filter(Boolean) : [];
            if (items.length < 2) issues.push(`Math-блок #${idx + 1}: нужно минимум два шага.`);
          }
          if (kind === 'match') {
            const left = Array.isArray(b?.matchLeftItems) ? b.matchLeftItems.filter((x) => String(x?.text || '').trim()) : [];
            const right = Array.isArray(b?.matchRightItems) ? b.matchRightItems.filter((x) => String(x?.text || '').trim()) : [];
            const pairs = Array.isArray(b?.matchPairs) ? b.matchPairs.filter((x) => String(x?.leftKey || '').trim() && String(x?.rightKey || '').trim()) : [];
            if (!left.length || !right.length || !pairs.length) issues.push(`Math-блок #${idx + 1}: заполни элементы и пары.`);
          }
        });
      }

      if (!Number.isFinite(Number(mathSettings?.maxAttempts)) || Number(mathSettings?.maxAttempts) < 1) {
        issues.push('У math-задания количество попыток должно быть не меньше 1.');
      }
      const mathPassPercent = Number(mathSettings?.passPercent);
      if (!Number.isFinite(mathPassPercent) || mathPassPercent < 0 || mathPassPercent > 100) {
        issues.push('У math-задания проходной процент должен быть от 0 до 100.');
      }
    }

    return [...new Set(issues)];
  }, [title, description, type, difficulty, rating, isHidden, isAiDraft, lifecycleStatus, testCases, imageTestReferenceKey, imageTestThreshold, testQuestions, testSettings, mathBlocks, mathSettings]);

  useEffect(() => {
    if (saveIssues.length > 0) {
      setSaveIssues([]);
      setErr('');
    }
  
  }, [title, description, type, difficulty, rating, testCases, imageTestReferenceKey, imageTestThreshold, testQuestions, testSettings, mathBlocks, mathSettings]);

  const addTest = () =>
    setTestCases((prev) => [
      ...prev,
      { input: "", expectedOutput: "", isHidden: false },
    ]);

  const removeTest = (idx) =>
    setTestCases((prev) => prev.filter((_, i) => i !== idx));

  const changeTest = (idx, field, value) => {
    setTestCases((prev) => {
      const next = [...prev];
      next[idx] = { ...next[idx], [field]: value };
      return next;
    });
  };

  const save = async () => {
    setBusy(true);
    setErr("");
    setSaveIssues([]);

    if (validationIssues.length > 0) {
      setErr("Задание ещё не готово к сохранению.");
      setSaveIssues(validationIssues);
      notify.warn(`Исправь ошибки в задании: ${validationIssues.length}`);
      setBusy(false);
      return;
    }

    try {

      const payload = {
        title: title.trim(),
        description,
        type: (type || "code-test").trim(),
        allowedLanguages: (LANGS_BY_TYPE[(type || "").trim()] && Array.isArray(allowedLanguages) && allowedLanguages.length > 0) ? allowedLanguages : null,
        tags: (tags || "").trim(),
        difficulty: Number(difficulty) || 1,
        rating: Number(rating) >= 0 ? Number(rating) : 1,
        isHidden,
        isAiDraft,
        lifecycleStatus: isHidden ? (lifecycleStatus === "published" ? "draft" : lifecycleStatus) : "published",

        
        codeForbiddenCalls: (["code-test", "image-test"].includes((type || "").trim()))
          ? (codeForbiddenCallsText || "")
              .replace(/\r/g, "")
              .split("\n")
              .map((x) => x.trim())
              .filter((x) => x.length > 0)
          : [],
        codeRequiredCalls: (["code-test", "image-test"].includes((type || "").trim()))
          ? (codeRequiredCallsText || "")
              .replace(/\r/g, "")
              .split("\n")
              .map((x) => x.trim())
              .filter((x) => x.length > 0)
          : [],
        
        
        testCases:
          (type || "").trim() === "code-test"
            ? testCases.map((t) => ({
                input: t.input ?? "",
                expectedOutput: t.expectedOutput ?? "",
                isHidden: !!t.isHidden,
              }))
            : [],

        
        imageTestReferenceKey:
          (type || "").trim() === "image-test" ? imageTestReferenceKey || null : null,
        imageTestSimilarityThreshold:
          (type || "").trim() === "image-test" ? Number(imageTestThreshold) || 90 : null,
      };

      await updateAssignment(assignmentId, payload);

      
      if ((type || "").trim() === "test") {
        
        
        const cleanedQuestions = (testQuestions || []).map((q) => {
          const aa = Array.isArray(q?.acceptedAnswers)
            ? q.acceptedAnswers
                .map((x) => (typeof x === "string" ? x : ""))
                .map((x) => x.replace(/\r/g, ""))
                .filter((x) => x.trim().length > 0)
            : [];
          return { ...q, acceptedAnswers: aa };
        });
        await saveTaskTestEdit(assignmentId, {
          settings: testSettings,
          questions: cleanedQuestions,
        });
      }

      if ((type || "").trim() === "math") {
        const cleanedBlocks = (mathBlocks || []).map((b) => ({
          ...b,
          acceptedAnswers: Array.isArray(b?.acceptedAnswers)
            ? b.acceptedAnswers
                .map((x) => (typeof x === "string" ? x : ""))
                .map((x) => x.replace(/\r/g, ""))
                .filter((x) => x.trim().length > 0)
            : [],
          orderItems: Array.isArray(b?.orderItems)
            ? b.orderItems
                .map((x) => (typeof x === "string" ? x : ""))
                .map((x) => x.replace(/\r/g, ""))
                .filter((x) => x.trim().length > 0)
            : [],
        }));
        await saveMathTaskEdit(assignmentId, {
          settings: mathSettings,
          blocks: cleanedBlocks,
        });
      }
      notify.success("Изменения сохранены");
      nav(`/assignment/${assignmentId}`);
    } catch (e) {
      
      if (e?.response?.status === 403) {
        notifyOnce("no-edit-assignment", () =>
          notify.error(e.response?.data?.message || "Нельзя редактировать данное задание")
        );
        nav(`/assignment/${assignmentId}`, { replace: true });
        return;
      }
      const parsed = extractApiErrorMessages(e, "Ошибка сохранения");
      setErr(parsed.primaryMessage || "Ошибка сохранения");
      setSaveIssues(parsed.messages || []);
      handleApiError(e, notify, "Ошибка сохранения");
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    const ok = await notify.confirm({
      title: "Удалить задание?",
      message: "Действие необратимо.",
      okText: "Удалить",
      cancelText: "Отмена",
    });
    if (!ok) return;

    try {

      await deleteAssignment(assignmentId);
      notify.success("Задание удалено");
      if (courseId) nav(`/course/${courseId}`);
      else nav(-1);
    } catch (e) {
      if (e?.response?.status === 403) {
        notifyOnce("no-edit-assignment", () =>
          notify.error("Нельзя удалять чужие задания")
        );
        nav(`/assignment/${assignmentId}`, { replace: true });
        return;
      }
      handleApiError(e, notify, "Ошибка удаления");
    }
  };

  if (loading) {
    return (
      <Layout fullWidth>
        <div className="text-neutral-500">Загрузка…</div>
      </Layout>
    );
  }

  return (
    <Layout fullWidth>
      <div className="mb-5 flex flex-wrap items-center gap-2">
        {courseId && (
          <Button
            variant="ghost"
            className="inline-flex items-center gap-2"
            onClick={() => nav(`/course/${courseId}`)}
          >
            <ArrowLeft size={16} /> к заданиям курса
          </Button>
        )}
        {isAdmin && (
          <Button
            variant="outline"
            className="inline-flex items-center gap-2"
            onClick={() => nav(`/admin/ai?assignmentId=${assignmentId}${courseId ? `&courseId=${courseId}` : ''}`)}
            title="Открыть AI-ассистент для этого задания"
          >
            <Bot size={16} /> AI по заданию
          </Button>
        )}
      </div>

      {err && <div className="text-red-500 font-medium mb-4">{err}</div>}

      

      <div className="space-y-5">
          <Card>
            <div className="flex items-start justify-between gap-4">
              <div>
                <h2 className="text-xl font-semibold mb-1">Готовность задания</h2>
                <div className="text-sm text-neutral-500">Здесь видно, что ещё нужно заполнить до сохранения.</div>
              </div>
              <div className="flex flex-wrap items-center gap-2">
                <div className={`text-sm font-medium ${validationIssues.length === 0 ? 'text-emerald-600' : 'text-amber-600'}`}>
                  {validationIssues.length === 0 ? 'Готово к сохранению' : `Нужно исправить: ${validationIssues.length}`}
                </div>
              </div>
            </div>

            {validationIssues.length === 0 ? (
              <div className="mt-4 rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-700 dark:border-emerald-900/40 dark:bg-emerald-950/30 dark:text-emerald-300">
                Всё основное заполнено. Можно сохранять задание.
              </div>
            ) : (
              <div className="mt-4 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 dark:border-amber-900/40 dark:bg-amber-950/30">
                <div className="text-sm font-medium text-amber-800 dark:text-amber-300 mb-2">Что ещё не заполнено:</div>
                <ul className="list-disc pl-5 space-y-1 text-sm text-amber-900 dark:text-amber-200">
                  {validationIssues.map((issue) => (
                    <li key={issue}>{issue}</li>
                  ))}
                </ul>
              </div>
            )}

            {saveIssues.length > 0 && (
              <div className="mt-4 rounded-xl border border-red-200 bg-red-50 px-4 py-3 dark:border-red-900/40 dark:bg-red-950/30">
                <div className="text-sm font-medium text-red-800 dark:text-red-300 mb-2">Почему сохранение не прошло:</div>
                <ul className="list-disc pl-5 space-y-1 text-sm text-red-900 dark:text-red-200">
                  {saveIssues.map((issue, idx) => (
                    <li key={`${issue}-${idx}`}>{issue}</li>
                  ))}
                </ul>
              </div>
            )}
          </Card>

          <Card>
            <h2 className="text-xl font-semibold mb-4">Основное</h2>
            <div className="grid sm:grid-cols-2 gap-4">
              <Field label="Название">
                <Input value={title} onChange={(e) => setTitle(e.target.value)} />
              </Field>

              <Field label="Тип">
                <Select value={type} onChange={(e) => setType(e.target.value)}>
                  <option value="code-test">code-test</option>
                  <option value="image-test">image-test</option>
                  <option value="test">test</option>
                  <option value="math">math</option>
                </Select>
              </Field>

              {(type === "code-test" || type === "image-test") && (
                <Field label="Разрешённые языки (если пусто — разрешены все)">
                  <div className="flex flex-col gap-2">
                    <div className="flex flex-wrap items-center gap-2">
                      <Select value={langToAdd} onChange={(e) => setLangToAdd(e.target.value)}>
                        <option value="">-- выбрать язык --</option>
                        {(LANGS_BY_TYPE[(type || "").trim()] || [])
                          .filter((o) => !allowedLanguages.includes(o.value))
                          .map((o) => (
                            <option key={o.value} value={o.value}>{o.label}</option>
                          ))}
                      </Select>
                      <Button
                        type="button"
                        className="btn-outline"
                        disabled={!langToAdd}
                        onClick={() => {
                          if (!langToAdd) return;
                          setAllowedLanguages((prev) => (prev.includes(langToAdd) ? prev : [...prev, langToAdd]));
                          setLangToAdd("");
                        }}
                      >
                        <PlusCircle size={16} /> Добавить
                      </Button>
                    </div>

                    {allowedLanguages.length > 0 && (
                      <div className="flex flex-wrap gap-2">
                        {allowedLanguages.map((v) => {
                          const o = (LANGS_BY_TYPE[(type || "").trim()] || []).find((x) => x.value === v);
                          const label = o?.label || v;
                          return (
                            <span
                              key={v}
                              className="inline-flex items-center gap-2 px-3 py-1 rounded-full border border-neutral-200 dark:border-neutral-800 text-sm"
                            >
                              {label}
                              <button
                                type="button"
                                className="text-red-600 hover:text-red-700"
                                onClick={() => setAllowedLanguages((prev) => prev.filter((x) => x !== v))}
                                title="Удалить"
                              >
                                ✕
                              </button>
                            </span>
                          );
                        })}
                      </div>
                    )}
                  </div>
                </Field>
              )}


              {(["code-test", "image-test"].includes((type || "").trim())) && (
                <div className="sm:col-span-2">
                  <Card className="p-4">
                    <div className="grid md:grid-cols-2 gap-4">
                      <Field label="Запрещённые (если найдено — решение отклоняется)">
                        <Textarea
                          rows={7}
                          value={codeForbiddenCallsText}
                          onChange={(e) => setCodeForbiddenCallsText(e.target.value)}
                          placeholder={"Process.Start\n__import__\nstd::sort"}
                        />
                        <div className="mt-2 flex flex-wrap gap-2">
                          {(codeForbiddenCallsText || "")
                            .replace(/\r/g, "")
                            .split("\n")
                            .map((x) => x.trim())
                            .filter((x) => x.length > 0)
                            .slice(0, 24)
                            .map((x) => (
                              <span
                                key={x}
                                className="px-2 py-1 rounded-full text-xs border border-neutral-200 dark:border-neutral-800 bg-[rgb(var(--card))]"
                                title={x}
                              >
                                {x}
                              </span>
                            ))}
                        </div>
                      </Field>

                      <Field label="Ожидаемые (каждое правило должно встретиться)">
                        <Textarea
                          rows={7}
                          value={codeRequiredCallsText}
                          onChange={(e) => setCodeRequiredCallsText(e.target.value)}
                          placeholder={"solve"}
                        />
                        <div className="mt-2 flex flex-wrap gap-2">
                          {(codeRequiredCallsText || "")
                            .replace(/\r/g, "")
                            .split("\n")
                            .map((x) => x.trim())
                            .filter((x) => x.length > 0)
                            .slice(0, 24)
                            .map((x) => (
                              <span
                                key={x}
                                className="px-2 py-1 rounded-full text-xs border border-neutral-200 dark:border-neutral-800 bg-[rgb(var(--card))]"
                                title={x}
                              >
                                {x}
                              </span>
                            ))}
                        </div>
                      </Field>
                    </div>
                  </Card>
                </div>
              )}


              <Field label="Сложность">
                <Select
                  value={difficulty}
                  onChange={(e) => setDifficulty(Number(e.target.value))}
                >
                  <option value={1}>легко</option>
                  <option value={2}>средне</option>
                  <option value={3}>сложно</option>
                </Select>
              </Field>

              <Field label="Рейтинг">
                <Input
                  type="number"
                  min={0}
                  value={rating}
                  onChange={(e) => setRating(e.target.value)}
                />
              </Field>

              <Field label="Теги (через запятую)">
                <Input value={tags} onChange={(e) => setTags(e.target.value)} />
              </Field>

              <Field label="Метки">
                <div className="flex flex-wrap gap-2">
                  <label className="inline-flex cursor-pointer items-center gap-2 rounded-full border border-[rgba(var(--border)/0.65)] px-3 py-2 text-sm">
                    <input
                      type="checkbox"
                      checked={isAiDraft}
                      onChange={(e) => setIsAiDraft(e.target.checked)}
                    />
                    AI-черновик
                  </label>
                  <label className="inline-flex cursor-pointer items-center gap-2 rounded-full border border-[rgba(var(--border)/0.65)] px-3 py-2 text-sm">
                    <input
                      type="checkbox"
                      checked={isHidden}
                      onChange={(e) => {
                        const checked = e.target.checked;
                        setIsHidden(checked);
                        setLifecycleStatus(checked ? (lifecycleStatus === "published" ? "draft" : lifecycleStatus) : "published");
                      }}
                    />
                    Скрыто
                  </label>
                </div>
              </Field>

              <div className="sm:col-span-2">
                <Field label="Условие задания (редактор)">
                  <div className="min-h-[60vh]">
                    <StatementEditor value={description} onChange={setDescription} />
                  </div>
                </Field>
              </div>
            </div>
          </Card>

          {type === 'code-test' && (
            <Card>
              <div className="flex items-center justify-between mb-3">
                <h2 className="text-xl font-semibold">Тест-кейсы</h2>
                <Button className="btn-outline" onClick={addTest}>
                  <PlusCircle size={16} /> Добавить тест
                </Button>
              </div>

              <div className="space-y-4">
                {testCases.map((t, idx) => (
                  <div
                    key={idx}
                    className="rounded-xl border border-neutral-200 dark:border-neutral-800 p-4 bg-[rgb(var(--card))]"
                  >
                    <div className="grid sm:grid-cols-2 gap-4">
                      <Field label="Input">
                        <Textarea
                          rows={4}
                          value={t.input}
                          onChange={(e) => changeTest(idx, "input", e.target.value)}
                        />
                      </Field>
                      <Field label="Expected Output">
                        <Textarea
                          rows={4}
                          value={t.expectedOutput}
                          onChange={(e) =>
                            changeTest(idx, "expectedOutput", e.target.value)
                          }
                        />
                      </Field>
                      <label className="flex items-center gap-2 text-sm sm:col-span-2">
                        <input
                          type="checkbox"
                          checked={t.isHidden}
                          onChange={(e) => changeTest(idx, "isHidden", e.target.checked)}
                        />
                        Скрытый
                      </label>
                    </div>
                    <div className="mt-3 flex justify-end">
                      <Button
                        variant="outline"
                        className="text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20"
                        onClick={() => removeTest(idx)}
                      >
                        <Trash2 size={16} /> Удалить тест
                      </Button>
                    </div>
                  </div>
                ))}
              </div>
            </Card>
          )}

          {type === "image-test" && (
            <Card>
              <h2 className="text-xl font-semibold mb-2">Image-test</h2>
              <p className="text-sm text-neutral-600 dark:text-neutral-300 mb-4">
                Этот тип задания проверяется сравнением картинки. Эталон хранится приватно.
              </p>

              <div className="grid md:grid-cols-2 gap-4">
                <Field label="Порог совпадения, %">
                  <Input
                    type="number"
                    min={0}
                    max={100}
                    value={imageTestThreshold}
                    onChange={(e) => setImageTestThreshold(Number(e.target.value))}
                  />
                </Field>

                <div>
                  <div className="label mb-2">Эталонная картинка</div>
                  <div className="flex flex-wrap items-center gap-2">
                    <Button
                      type="button"
                      className="btn-outline"
                      onClick={async () => {
                        const input = document.createElement("input");
                        input.type = "file";
                        input.accept = "image/*";
                        input.onchange = async () => {
                          const file = input.files?.[0];
                          if (!file) return;
                          try {

                            const r = await uploadImageTestReference(
                              assignmentId,
                              file,
                              Number(imageTestThreshold) || 90
                            );
                            setImageTestReferenceKey(r.key || "");
                            if (typeof r.threshold === "number") setImageTestThreshold(r.threshold);
                            notify.success("Эталон загружен");
                          } catch (e) {
                            handleApiError(e, notify, "Не удалось загрузить эталон");
                          }
                        };
                        input.click();
                      }}
                    >
                      Загрузить эталон
                    </Button>
                    {imageTestReferenceKey && (
                      <span className="text-xs text-neutral-500 break-all">
                        {imageTestReferenceKey}
                      </span>
                    )}
                  </div>

                  {imageTestReferenceKey ? (
                    <img
                      className="mt-3 max-h-64 rounded-xl border border-neutral-200 dark:border-neutral-800"
                      src={`/api/private-files/${encodeURIComponent(imageTestReferenceKey)}`}
                      alt="Эталон"
                    />
                  ) : (
                    <div className="mt-3 text-sm text-neutral-500">
                      Эталон ещё не загружен.
                    </div>
                  )}
                </div>
              </div>
            </Card>
          )}


          {type === 'test' && (
            <TaskTestEditor
              settings={testSettings}
              setSettings={setTestSettings}
              questions={testQuestions}
              setQuestions={setTestQuestions}
            />
          )}

          {type === 'math' && (
            <MathTaskEditor
              settings={mathSettings}
              setSettings={setMathSettings}
              blocks={mathBlocks}
              setBlocks={setMathBlocks}
            />
          )}
        </div>

      
      <div className="sticky bottom-0 z-10 -mx-4 sm:-mx-6 lg:-mx-8 px-4 sm:px-6 lg:px-8 py-3 bg-[rgb(var(--bg))]/80 backdrop-blur border-t border-neutral-200/60 dark:border-neutral-800/60 mt-6">
        <div className="flex items-center justify-end gap-2">
          <Button onClick={save} disabled={busy}>
            <Save size={16} /> {busy ? "Сохраняю…" : "Сохранить"}
          </Button>
          <Button
            variant="outline"
            onClick={remove}
            className="text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20"
          >
            <Trash2 size={16} /> Удалить
          </Button>
        </div>
      </div>
    </Layout>
  );
}
