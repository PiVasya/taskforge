import React, { useEffect, useState } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";

import Layout from "../components/Layout";
import { useNotify } from "../components/notify/NotifyProvider";
import { handleApiError } from "../utils/handleApiError";
import { notifyOnce } from "../utils/notifyOnce";

import { getAssignment, updateAssignment, deleteAssignment } from "../api/assignments";

import { Card, Button, Field, Input, Textarea, Select } from "../components/ui";
import { Save, Trash2, ArrowLeft, PlusCircle } from "lucide-react";

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
  const [testCases, setTestCases] = useState([]);

  const [courseId, setCourseId] = useState(null);

  useEffect(() => {
    (async () => {
      try {
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
        setTestCases(
          Array.isArray(a.testCases) && a.testCases.length
            ? a.testCases.map((t) => ({
                input: t.input ?? "",
                expectedOutput: t.expectedOutput ?? "",
                isHidden: !!t.isHidden,
              }))
            : [{ input: "", expectedOutput: "", isHidden: false }]
        );
      } catch (e) {
        handleApiError(e, notify, "Ошибка загрузки задания");
      } finally {
        setLoading(false);
      }
    })();
  }, [assignmentId, nav, notify]);

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
      const payload = {
        title: title.trim(),
        description,
        type: (type || "code-test").trim(),
        tags: (tags || "").trim(),
        difficulty: Number(difficulty) || 1,
        testCases: testCases.map((t) => ({
          input: t.input ?? "",
          expectedOutput: t.expectedOutput ?? "",
          isHidden: !!t.isHidden,
        })),
      };

      await updateAssignment(assignmentId, payload);
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
      <Layout>
        <div className="text-slate-500">Загрузка…</div>
      </Layout>
    );
  }

  return (
    <Layout>
      {courseId && (
        <Link
          to={`/course/${courseId}`}
          className="inline-flex items-center gap-2 text-brand-600 hover:underline mb-5"
        >
          <ArrowLeft size={16} /> к заданиям курса
        </Link>
      )}

      {err && <div className="text-red-500 font-medium mb-4">{err}</div>}

      <div className="grid lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 space-y-5">
          <Card>
            <h2 className="text-xl font-semibold mb-4">Основное</h2>
            <div className="grid sm:grid-cols-2 gap-4">
              <Field label="Название">
                <Input value={title} onChange={(e) => setTitle(e.target.value)} />
              </Field>

              <Field label="Тип">
                <Select value={type} onChange={(e) => setType(e.target.value)}>
                  <option value="code-test">code-test</option>
                  <option value="quiz" disabled>
                    quiz (скоро)
                  </option>
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

              <Field label="Теги (через запятую)">
                <Input value={tags} onChange={(e) => setTags(e.target.value)} />
              </Field>

              <div className="sm:col-span-2">
                <Field label="Описание (markdown/html)">
                  <Textarea
                    rows={10}
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                  />
                </Field>
              </div>
            </div>
          </Card>

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
                  className="rounded-xl border border-slate-200 dark:border-slate-800 p-4 bg-white/60 dark:bg-slate-900/40"
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
        </div>

        <div className="space-y-4">
          <Card>
            <div className="flex gap-2">
              <Button onClick={save} disabled={busy} className="flex-1">
                <Save size={16} /> {busy ? "Сохраняю…" : "Сохранить"}
              </Button>
              <Button
                variant="outline"
                onClick={remove}
                className="flex-1 text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20"
              >
                <Trash2 size={16} /> Удалить
              </Button>
            </div>
          </Card>

          <Card>
            <div className="text-sm text-slate-500">
              Подсказка: используйте публичные и скрытые тесты, чтобы проверки были
              надёжными.
            </div>
          </Card>
        </div>
      </div>
    </Layout>
  );
}
