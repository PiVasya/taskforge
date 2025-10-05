import React, { useEffect, useMemo, useState } from "react";
import { useNavigate, useParams, Link, useSearchParams } from "react-router-dom";

import Layout from "../components/Layout";
import { Card, Button, Input } from "../components/ui";

import {
  getAssignmentsByCourse,
  createAssignment,
  updateAssignmentSort,
} from "../api/assignments";
import { Plus, Layers, CheckCircle2, ArrowUp, ArrowDown } from "lucide-react";
import IfEditor from "../components/IfEditor";
import { useNotify } from "../components/notify/NotifyProvider";
import { handleApiError } from "../utils/handleApiError";
import { notifyOnce } from "../utils/notifyOnce";

const SORT_OPTIONS = [
  { v: "default", label: "Стандартный (по полю Sort)" },
  { v: "title_asc", label: "A → Я" },
  { v: "title_desc", label: "Я → A" },
  { v: "created_desc", label: "Сначала новые" },
  { v: "created_asc", label: "Сначала старые" },
];

export default function CourseAssignmentsPage() {
  const { courseId } = useParams();
  const nav = useNavigate();
  const [params, setParams] = useSearchParams();
  const notify = useNotify();

  const [items, setItems] = useState([]);
  const [q, setQ] = useState("");
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");

  const sortMode = params.get("sort") || "default";

  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        setErr("");
        const data = await getAssignmentsByCourse(courseId); // сервер теперь отдаёт canEdit
        const norm = (data || []).map((x, i) => ({
          ...x,
          sort: typeof x.sort === "number" ? x.sort : i,
        }));
        setItems(norm);
      } catch (e) {
        setErr(e.message || "Ошибка загрузки");
      } finally {
        setLoading(false);
      }
    })();
  }, [courseId]);

  const filtered = useMemo(() => {
    const s = (items || []).filter(
      (x) =>
        (x.title || "").toLowerCase().includes(q.toLowerCase()) ||
        (x.tags || "").toLowerCase().includes(q.toLowerCase())
    );
    const byTitle = (a, b, dir = 1) =>
      (a.title || "").localeCompare(b.title || "", undefined, {
        sensitivity: "base",
      }) * dir;
    // важно: скобки — сначала разница дат, потом умножение на dir
    const byCreated = (a, b, dir = 1) =>
      (new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime()) * dir;
    const bySort = (a, b) => (a.sort ?? 0) - (b.sort ?? 0);

    switch (sortMode) {
      case "title_asc":
        return [...s].sort((a, b) => byTitle(a, b, +1));
      case "title_desc":
        return [...s].sort((a, b) => byTitle(a, b, -1));
      case "created_asc":
        return [...s].sort((a, b) => byCreated(a, b, +1));
      case "created_desc":
        return [...s].sort((a, b) => byCreated(a, b, -1));
      case "default":
      default:
        return [...s].sort(bySort);
    }
  }, [items, q, sortMode]);

  const setSortMode = (mode) => {
    const next = new URLSearchParams(params);
    next.set("sort", mode);
    setParams(next, { replace: true });
  };

  const swapByIndex = async (i, j) => {
    if (i < 0 || j < 0 || i >= filtered.length || j >= filtered.length) return;

    const a = filtered[i];
    const b = filtered[j];

    // если нет прав — предупреждаем и выходим
    if (!a.canEdit || !b.canEdit) {
      notifyOnce("no-edit-sort", () =>
        notify.warn("Вы не владелец курса — менять порядок заданий нельзя")
      );
      return;
    }

    const newItems = items.map((x) => {
      if (x.id === a.id) return { ...x, sort: b.sort ?? j };
      if (x.id === b.id) return { ...x, sort: a.sort ?? i };
      return x;
    });
    setItems(newItems);

    try {
      await Promise.all([
        updateAssignmentSort(a.id, b.sort ?? j),
        updateAssignmentSort(b.id, a.sort ?? i),
      ]);
    } catch (e) {
      handleApiError(e, notify, "Не удалось изменить порядок");
      try {
        const data = await getAssignmentsByCourse(courseId);
        const norm = (data || []).map((x, k) => ({
          ...x,
          sort: typeof x.sort === "number" ? x.sort : k,
        }));
        setItems(norm);
      } catch {}
    }
  };

  const handleCreate = async () => {
    // быстрый UX-гард: по первому элементу понимаем, чужой курс или нет
    if (items.length > 0 && items[0].canEdit === false) {
      notifyOnce("no-edit-course", () =>
        notify.warn("Вы не владелец курса — создавать задания нельзя")
      );
      return;
    }
    try {
      const payload = {
        title: "Новое задание",
        description: "Опишите постановку задачи…",
        type: "code-test",
        difficulty: 1,
        testCases: [{ input: "2 4", expectedOutput: "6", isHidden: false }],
        tags: "ОАИП",
        sort: items.length,
      };
      const res = await createAssignment(courseId, payload);
      const id = res && res.id;
      if (id) nav(`/assignment/${id}/edit`);
    } catch (e) {
      if (e?.response?.status === 403) {
        notifyOnce("no-edit-course", () =>
          notify.error(e?.response?.data?.message || "Создание запрещено")
        );
        return;
      }
      handleApiError(e, notify, "Не удалось создать задание");
    }
  };

  return (
    <Layout>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-semibold flex items-center gap-2">
          <Layers size={22} /> Задания курса
        </h1>

        <div className="flex items-center gap-3">
          <div>
            <select
              value={sortMode}
              onChange={(e) => setSortMode(e.target.value)}
              className="border rounded-lg px-3 py-2
                        bg-white text-slate-900 border-slate-300
                        dark:bg-slate-800 dark:text-slate-100 dark:border-slate-600
                        focus:outline-none focus:ring-2 focus:ring-sky-500/60"
              title="Сортировка"
            >
              {SORT_OPTIONS.map((o) => (
                <option key={o.v} value={o.v}>
                  {o.label}
                </option>
              ))}
            </select>
          </div>

          <IfEditor>
            <Button onClick={handleCreate}>
              <Plus size={16} /> Создать
            </Button>
          </IfEditor>
        </div>
      </div>

      <Card className="mb-6">
        <div className="flex items-center gap-3">
          <div className="relative flex-1">
            <Input
              placeholder="Поиск по названию или тегам"
              value={q}
              onChange={(e) => setQ(e.target.value)}
            />
          </div>
        </div>
      </Card>

      {err && <div className="text-red-500 mb-4">{err}</div>}
      {loading && <div className="text-slate-500">Загрузка…</div>}

      <div className="grid md:grid-cols-2 xl:grid-cols-3 gap-5">
        {filtered.map((a, idx) => {
          const solved = !!a.solvedByCurrentUser;

          const ViewWrap = ({ children }) => (
            <Link to={`/assignment/${a.id}`} className="block group">
              {children}
            </Link>
          );
          const EditWrap = ({ children }) => (
            <Link to={`/assignment/${a.id}/edit`} className="block group">
              {children}
            </Link>
          );

          const EditorToolbar =
            sortMode === "default" ? (
              <IfEditor>
                <div className="flex items-center gap-1">
                  <button
                    type="button"
                    className="px-2 py-1 rounded-lg border hover:bg-slate-50"
                    title="Выше"
                    onClick={(e) => {
                      e.preventDefault();
                      swapByIndex(idx, idx - 1);
                    }}
                  >
                    <ArrowUp size={16} />
                  </button>
                  <button
                    type="button"
                    className="px-2 py-1 rounded-lg border hover:bg-slate-50"
                    title="Ниже"
                    onClick={(e) => {
                      e.preventDefault();
                      swapByIndex(idx, idx + 1);
                    }}
                  >
                    <ArrowDown size={16} />
                  </button>
                </div>
              </IfEditor>
            ) : null;

          const CardInner = (
            <Card
              className={
                "transition hover:shadow-lg " +
                (solved ? "border-emerald-400/40 bg-emerald-500/5" : "")
              }
            >
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0 grow">
                  <div className="flex items-center gap-2">
                    <div
                      className={
                        "text-lg font-semibold truncate " +
                        (solved ? "text-emerald-600" : "")
                      }
                      title={a.title}
                    >
                      {a.title}
                    </div>
                    {solved && (
                      <span className="inline-flex items-center gap-1 rounded-xl border border-emerald-400/40 bg-emerald-500/10 px-2 py-0.5 text-emerald-600 text-xs">
                        <CheckCircle2 size={14} />
                        Решено
                      </span>
                    )}
                  </div>

                  {a.description && (
                    <p className="text-sm text-slate-500 line-clamp-2 mt-1">
                      {a.description}
                    </p>
                  )}
                  {a.tags && (
                    <div className="mt-2 text-xs text-slate-400">{a.tags}</div>
                  )}
                </div>

                {EditorToolbar}
              </div>
            </Card>
          );

          // В режим /edit ведём только если canEdit === true
          return (
            <IfEditor key={a.id} otherwise={<ViewWrap>{CardInner}</ViewWrap>}>
              {a.canEdit ? (
                <EditWrap>{CardInner}</EditWrap>
              ) : (
                <ViewWrap>{CardInner}</ViewWrap>
              )}
            </IfEditor>
          );
        })}
      </div>

      {!loading && filtered.length === 0 && (
        <div className="card-muted p-8 text-center text-slate-500 mt-6">
          Пока заданий нет. Создайте первое ✨
        </div>
      )}
    </Layout>
  );
}
