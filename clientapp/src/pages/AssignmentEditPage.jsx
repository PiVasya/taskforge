import React, { useEffect, useState } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";

import Layout from "../components/Layout";
import { useNotify } from "../components/notify/NotifyProvider";
import { handleApiError } from "../utils/handleApiError";
import { notifyOnce } from "../utils/notifyOnce";

import { getAssignment, updateAssignment, deleteAssignment } from "../api/assignments";
import { getTaskTestEdit, saveTaskTestEdit } from "../api/taskTests";

import { Card, Button, Field, Input, Textarea, Select } from "../components/ui";
import { Save, Trash2, ArrowLeft, PlusCircle } from "lucide-react";
import TaskTestEditor from "./TaskTestEditor";
import StatementEditor from "../components/tiptap/StatementEditor";
import { uploadImageTestReference } from "../api/imageTests";



// Разрешённые языки решения (настраиваются в задании)
const LANGS_BY_TYPE = {
  "code-test": [
    { value: "cpp", label: "C++" },
    { value: "python", label: "Python" },
    { value: "csharp", label: "C#" },
    { value: "javascript", label: "JavaScript" },
    { value: "pascal", label: "Pascal" },
    { value: "java", label: "Java" },
  ],
  // image-test поддерживает только языки, которые умеют рендерить картинку
  "image-test": [
    { value: "python", label: "Python" },
    { value: "pascal", label: "Pascal" },
  ],
};

export default function AssignmentEditPage() {
  const { assignmentId } = useParams();
  const nav = useNavigate();
  const notify = useNotify();

  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState("");

  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [type, setType] = useState("code-test");
  const [tags, setTags] = useState("");
  const [difficulty, setDifficulty] = useState(1);
  const [rating, setRating] = useState(1);
  const [testCases, setTestCases] = useState([]);

  const [testSettings, setTestSettings] = useState({
    shuffleQuestions: true,
    shuffleAnswers: true,
    maxAttempts: 1,
    passPercent: 60,
    allowReview: true,
    attemptTimeLimitsSeconds: [],
  });
  const [testQuestions, setTestQuestions] = useState([]);

  // image-test
  const [imageTestReferenceKey, setImageTestReferenceKey] = useState("");
  const [imageTestThreshold, setImageTestThreshold] = useState(90);

  // allowed languages
  const [allowedLanguages, setAllowedLanguages] = useState([]);
  const [langToAdd, setLangToAdd] = useState("");

  const [courseId, setCourseId] = useState(null);

  useEffect(() => {
    (async () => {
      try {
      // проверка языков
      if (LANGS_BY_TYPE[(type || "").trim()] && (!Array.isArray(allowedLanguages) || allowedLanguages.length === 0)) {
        notify.error("Выберите хотя бы один разрешённый язык");
        setBusy(false);
        return;
      }

        setLoading(true);
        setErr("");
        const a = await getAssignment(assignmentId); // должен вернуть { ..., canEdit, testCases, ... }

        if (!a?.canEdit) {
          notifyOnce("no-edit-assignment", () =>
            notify.warn("Нельзя редактировать данное задание")
          );
          nav(`/assignment/${assignmentId}`, { replace: true });
          return;
        }

        // заполняем форму
        setCourseId(a.courseId || null);
        setTitle(a.title || "");
        setDescription(a.description || "");
        setType(a.type || "code-test");
        setTags(a.tags || "");
        setDifficulty(Number(a.difficulty || 1));
        setRating(typeof a.rating === "number" ? a.rating : Number(a.rating || 1));
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

        // если это тест — подтягиваем настройки/вопросы
        if ((a.type || "").trim() === "test") {
          try {
      // проверка языков
      if (LANGS_BY_TYPE[(type || "").trim()] && (!Array.isArray(allowedLanguages) || allowedLanguages.length === 0)) {
        notify.error("Выберите хотя бы один разрешённый язык");
        setBusy(false);
        return;
      }

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
            // не блокируем редактор базовых полей
            console.warn('getTaskTestEdit failed', e2);
          }
        }
      } catch (e) {
        handleApiError(e, notify, "Ошибка загрузки задания");
      } finally {
        setLoading(false);
      }
    })();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assignmentId, nav]); // убрали notify из зависимостей


  // при смене типа — удаляем несовместимые языки
  useEffect(() => {
    const opts = LANGS_BY_TYPE[type] || null;
    if (!opts) {
      // для типа "test" языки не нужны
      setAllowedLanguages([]);
      setLangToAdd("");
      return;
    }
    const allowedSet = new Set(opts.map((x) => x.value));
    setAllowedLanguages((prev) => (Array.isArray(prev) ? prev.filter((x) => allowedSet.has(x)) : []));
    setLangToAdd("");
  }, [type]);

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
    try {
      // проверка языков
      if (LANGS_BY_TYPE[(type || "").trim()] && (!Array.isArray(allowedLanguages) || allowedLanguages.length === 0)) {
        notify.error("Выберите хотя бы один разрешённый язык");
        setBusy(false);
        return;
      }

      const payload = {
        title: title.trim(),
        description,
        type: (type || "code-test").trim(),
        allowedLanguages: (LANGS_BY_TYPE[(type || "").trim()] ? allowedLanguages : null),
        tags: (tags || "").trim(),
        difficulty: Number(difficulty) || 1,
        rating: Number(rating) >= 0 ? Number(rating) : 1,
        // для type=test на бэке тест-кейсы не нужны: просто отправляем пустой массив,
        // чтобы при смене типа старые тест-кейсы были удалены
        testCases:
          (type || "").trim() === "code-test"
            ? testCases.map((t) => ({
                input: t.input ?? "",
                expectedOutput: t.expectedOutput ?? "",
                isHidden: !!t.isHidden,
              }))
            : [],

        // image-test
        imageTestReferenceKey:
          (type || "").trim() === "image-test" ? imageTestReferenceKey || null : null,
        imageTestSimilarityThreshold:
          (type || "").trim() === "image-test" ? Number(imageTestThreshold) || 90 : null,
      };

      await updateAssignment(assignmentId, payload);

      // сохраняем тест (если type=test)
      if ((type || "").trim() === "test") {
        // В редакторе допустимых ответов мы не фильтруем пустые строки на лету (иначе Enter не работает),
        // поэтому перед сохранением чистим список ответов.
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
      notify.success("Изменения сохранены");
      nav(`/assignment/${assignmentId}`);
    } catch (e) {
      // 403 — чужое задание
      if (e?.response?.status === 403) {
        notifyOnce("no-edit-assignment", () =>
          notify.error(e.response?.data?.message || "Нельзя редактировать данное задание")
        );
        nav(`/assignment/${assignmentId}`, { replace: true });
        return;
      }
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
      // проверка языков
      if (LANGS_BY_TYPE[(type || "").trim()] && (!Array.isArray(allowedLanguages) || allowedLanguages.length === 0)) {
        notify.error("Выберите хотя бы один разрешённый язык");
        setBusy(false);
        return;
      }

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
        <div className="text-slate-500">Загрузка…</div>
      </Layout>
    );
  }

  return (
    <Layout fullWidth>
      {courseId && (
        <Link
          to={`/course/${courseId}`}
          className="inline-flex items-center gap-2 text-brand-600 hover:underline mb-5"
        >
          <ArrowLeft size={16} /> к заданиям курса
        </Link>
      )}

      {err && <div className="text-red-500 font-medium mb-4">{err}</div>}

      {/* верхнюю панель убрали: остаётся только нижняя (как просили) */}

      <div className="space-y-5">
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
                </Select>
              </Field>

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
                    className="rounded-xl border border-slate-200 dark:border-slate-800 p-4 bg-[rgb(var(--card))]"
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
              <p className="text-sm text-slate-600 dark:text-slate-300 mb-4">
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
      // проверка языков
      if (LANGS_BY_TYPE[(type || "").trim()] && (!Array.isArray(allowedLanguages) || allowedLanguages.length === 0)) {
        notify.error("Выберите хотя бы один разрешённый язык");
        setBusy(false);
        return;
      }

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
                      <span className="text-xs text-slate-500 break-all">
                        {imageTestReferenceKey}
                      </span>
                    )}
                  </div>

                  {imageTestReferenceKey ? (
                    <img
                      className="mt-3 max-h-64 rounded-xl border border-slate-200 dark:border-slate-800"
                      src={`/api/private-files/${encodeURIComponent(imageTestReferenceKey)}`}
                      alt="Эталон"
                    />
                  ) : (
                    <div className="mt-3 text-sm text-slate-500">
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
        </div>

      {/* нижняя панель (на всякий) */}
      <div className="sticky bottom-0 z-10 -mx-4 sm:-mx-6 lg:-mx-8 px-4 sm:px-6 lg:px-8 py-3 bg-[rgb(var(--bg))]/80 backdrop-blur border-t border-slate-200/60 dark:border-slate-800/60 mt-6">
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
