import React, { useEffect, useMemo, useState } from "react";
import { useNavigate, useParams, Link, useSearchParams } from "react-router-dom";

import Layout from "../components/Layout";
import { Card, Button, Input } from "../components/ui";

import { getCourse } from "../api/courses";

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

function previewAssignmentDescription(value) {
  if (!value) return '';
  const s = String(value);
  try {
    const json = JSON.parse(s);
    if (!json || typeof json !== 'object' || json.type !== 'doc') return s;

    const out = [];
    const walk = (n) => {
      if (!n) return;
      if (typeof n === 'string') return;
      if (n.type === 'text' && typeof n.text === 'string') out.push(n.text);
      if (Array.isArray(n.content)) n.content.forEach(walk);
    };
    walk(json);

    const text = out.join(' ').replace(/\s+/g, ' ').trim();
    return text || '...';
  } catch {
    return s;
  }
}

const SORT_OPTIONS = [
  { v: "default", label: "Стандартный" },
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
  const [courseCanEdit, setCourseCanEdit] = useState(true);
  const [q, setQ] = useState("");
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");

  // draft-значения для инпута "позиция" по каждому заданию
  // (чтобы можно было ввести число и применить по blur/Enter)
  const [posDraft, setPosDraft] = useState({});

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

  useEffect(() => {
    (async () => {
      try {
        const c = await getCourse(courseId);
        if (typeof c?.canEdit === 'boolean') setCourseCanEdit(!!c.canEdit);
      } catch {
        // ignore
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

  // Базовый порядок (позиции) всегда считаем по Sort и по ВСЕМ заданиям курса.
  // Это даёт корректные позиции даже когда включён поиск или другой режим сортировки.
  const orderedAll = useMemo(() => {
    const bySort = (a, b) => (a.sort ?? 0) - (b.sort ?? 0);
    return [...(items || [])].sort(bySort);
  }, [items]);

  const positionById = useMemo(() => {
    const m = new Map();
    orderedAll.forEach((x, idx) => m.set(x.id, idx + 1));
    return m;
  }, [orderedAll]);

  // Единый флаг прав редактирования курса.
  // Бэк отдаёт canEdit внутри каждого задания (как правило одинаковое для всех).
  // Если заданий ещё нет — разрешаем UI, а бэк всё равно не даст не-owner менять данные.
  const canEdit = useMemo(() => {
    if (!items || items.length === 0) return true;
    const any = items.find((x) => typeof x?.canEdit === "boolean");
    return any ? !!any.canEdit : true;
  }, [items]);

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

  // Перемещение задания на заданную позицию (1..N) в курсе.
  // Делается через пересчёт Sort для всех заданий курса (0..N-1).
  const moveToPosition = async (assignmentId, newPos1Based) => {
    if (!canEdit) {
      notify.error("Недостаточно прав");
      return;
    }
    if (sortMode !== "default") {
      notify.info("Изменение позиции доступно только в режиме сортировки: По порядку");
      return;
    }

    const n = orderedAll.length;
    let targetPos = parseInt(String(newPos1Based || ""), 10);
    if (!Number.isFinite(targetPos)) return;
    if (targetPos < 1) targetPos = 1;
    if (targetPos > n) targetPos = n;

    const curIndex = orderedAll.findIndex((x) => x.id === assignmentId);
    if (curIndex < 0) return;
    const newIndex = targetPos - 1;
    if (newIndex === curIndex) return;

    const nextOrder = [...orderedAll];
    const [moved] = nextOrder.splice(curIndex, 1);
    nextOrder.splice(newIndex, 0, moved);

    const oldSort = new Map();
    for (const x of orderedAll) oldSort.set(x.id, x.sort ?? 0);

    const newSort = new Map();
    nextOrder.forEach((x, idx) => newSort.set(x.id, idx));

    // Optimistic UI update
    setItems((prev) =>
      prev.map((x) => (newSort.has(x.id) ? { ...x, sort: newSort.get(x.id) } : x))
    );

    try {
      // Обновляем только то, что реально поменялось
      const changed = nextOrder
        .filter((x) => (oldSort.get(x.id) ?? 0) !== (newSort.get(x.id) ?? 0))
        .map((x) => ({ id: x.id, sort: newSort.get(x.id) ?? 0 }));

      await Promise.all(changed.map((x) => updateAssignmentSort(x.id, x.sort)));
      notify.success("Позиция обновлена");
    } catch (e) {
      console.error(e);
      notify.error("Не удалось изменить позицию");
      // откат/перезагрузка
      try {
        const list = await getAssignmentsByCourse(courseId);
        setItems(Array.isArray(list) ? list : []);
      } catch {
        // ignore
      }
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
            {courseCanEdit ? (
              <Button onClick={handleCreate}>
              <Plus size={16} /> Создать
              </Button>
            ) : null}
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
                <div className="flex items-center gap-2">
                  <div
                    className="flex items-center gap-2"
                    title="Позиция задания в курсе"
                    onClick={(e) => {
                      // не переходим по ссылке
                      e.preventDefault();
                      e.stopPropagation();
                    }}
                  >
                    <span className="text-xs text-slate-500">№</span>
                    <Input
                      type="number"
                      inputMode="numeric"
                      className="w-20"
                      min={1}
                      max={orderedAll.length}
                      value={
                      posDraft[a.id] ??
                        String(positionById.get(a.id) ?? "")
                      }
                      onChange={(e) =>
                        setPosDraft((p) => ({ ...p, [a.id]: e.target.value }))
                      }
                      onKeyDown={(e) => {
                        if (e.key === "Enter") {
                          e.currentTarget.blur();
                        }
                        if (e.key === "Escape") {
                          setPosDraft((p) => {
                            const next = { ...p };
                            delete next[a.id];
                            return next;
                          });
                          e.currentTarget.blur();
                        }
                      }}
                      onBlur={() => {
                        const raw = posDraft[a.id];
                        // если пользователь ничего не менял — просто выходим
                        if (raw === undefined) return;

                        // очищаем draft
                        setPosDraft((p) => {
                          const next = { ...p };
                          delete next[a.id];
                          return next;
                        });

                        const n = parseInt(String(raw), 10);
                        if (!Number.isFinite(n)) return;
                        moveToPosition(a.id, n);
                      }}
                    />
                  </div>

                  <button
                    type="button"
                    className="px-2 py-1 rounded-lg border hover:bg-slate-50"
                    title="Выше"
                    onClick={(e) => {
                      // Останавливаем всплытие события, чтобы клик по кнопке не переходил по ссылке
                      e.preventDefault();
                      e.stopPropagation();
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
                      // Останавливаем всплытие события, чтобы клик по кнопке не переходил по ссылке
                      e.preventDefault();
                      e.stopPropagation();
                      swapByIndex(idx, idx + 1);
                    }}
                  >
                    <ArrowDown size={16} />
                  </button>
                </div>
              </IfEditor>
            ) : null;

          const CardBody = (
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
                    {previewAssignmentDescription(a.description)}
                  </p>
                )}
                {a.tags && (
                  <div className="mt-2 text-xs text-slate-400">{a.tags}</div>
                )}
              </div>
            </div>
          );

          const CardBase = (
            <Card
              className={
                "transition hover:shadow-lg " +
                (solved ? "border-emerald-400/40 bg-emerald-500/5" : "")
              }
            >
              {CardBody}
            </Card>
          );

          // В редакторе НЕ кладём инпут/кнопки внутрь ссылки (иначе браузер ведёт себя странно)
          // Поэтому: карточка = div, тулбар сверху, а ссылкой делаем только тело.
          const EditorCard = (
            <Card
              className={
                "transition hover:shadow-lg " +
                (solved ? "border-emerald-400/40 bg-emerald-500/5" : "")
              }
            >
              <div className="flex items-start justify-end mb-3">{EditorToolbar}</div>
              <EditWrap>{CardBody}</EditWrap>
            </Card>
          );

          return (
            <IfEditor key={a.id} otherwise={<ViewWrap>{CardBase}</ViewWrap>}>
              {a.canEdit ? EditorCard : <ViewWrap>{CardBase}</ViewWrap>}
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
