import React, { useEffect, useMemo, useState } from "react";
import { useNavigate, useParams, Link, useSearchParams } from "react-router-dom";

import Layout from "../components/Layout";
import QuotaPill from "../components/QuotaPill";
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

  // тип создаваемого задания (по умолчанию — code-test)
  const [createType, setCreateType] = useState("code-test");

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
	      const type = createType;
	      const payload = {
        title: "Новое задание",
        description: "Опишите постановку задачи…",
	        type,
        difficulty: 1,
        rating: 1,
	        // Для code-test всегда кладём 1 тест по умолчанию, чтобы редактор не был пустым.
	        // Для остальных типов тест-кейсы не требуются.
	        testCases:
	          type === "code-test"
	            ? [{ input: "2 4", expectedOutput: "6", isHidden: false }]
	            : [],
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
      <div className="mb-6 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div className="flex min-w-0 items-center gap-2 sm:gap-3">
          <Button variant="outline" title="Вернуться к курсам" className="shrink-0" onClick={() => nav("/courses")}>
            ← Курсы
          </Button>
          <h1 className="min-w-0 text-xl font-semibold leading-tight sm:text-2xl flex items-center gap-2 flex-wrap">
            <Layers size={22} className="shrink-0" /> <span className="break-words">Задания курса</span>
          </h1>
        </div>

        <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 xl:flex xl:flex-wrap xl:items-center xl:justify-end xl:gap-3">
          <div className="min-w-0 xl:min-w-[170px]"><QuotaPill bucket="tasks" /></div>
          <div className="min-w-0 xl:min-w-[190px]">
            <select value={sortMode} onChange={(e) => setSortMode(e.target.value)} className="input w-full" title="Сортировка">
              {SORT_OPTIONS.map((o) => (
                <option key={o.v} value={o.v}>
                  {o.label}
                </option>
              ))}
            </select>
          </div>

          <IfEditor>
            {courseCanEdit ? (
              <>
                <select value={createType} onChange={(e) => setCreateType(e.target.value)} className="input w-full xl:w-auto" title="Тип создаваемого задания">
                  <option value="code-test">code-test</option>
                  <option value="test">test</option>
                  <option value="image-test">image-test</option>
                </select>

                <Button className="w-full sm:w-auto" onClick={handleCreate}>
                  <Plus size={16} /> Создать
                </Button>
              </>
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
      {loading && <div className="text-neutral-500">Загрузка…</div>}

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
	                <div
	                  className="mt-3 flex items-center justify-end gap-2 rounded-xl border border-white/10 bg-white/5 px-2 py-1 backdrop-blur sm:absolute sm:right-3 sm:top-3 sm:mt-0"
	                  onMouseDown={(e) => {
	                    // не даём карточке "увести" фокус/перейти по ссылке при клике по контролам
	                    e.preventDefault();
	                    e.stopPropagation();
	                  }}
	                >
	                  <div
	                    className="flex items-center gap-2"
	                    title="Позиция задания в курсе"
	                    onClick={(e) => {
	                      e.preventDefault();
	                      e.stopPropagation();
	                    }}
	                  >
	                    <span className="text-xs text-neutral-400">№</span>
	                    <Input
	                      type="number"
	                      inputMode="numeric"
	                      className="w-16 h-8 text-sm"
	                      min={1}
	                      max={orderedAll.length}
	                      value={posDraft[a.id] ?? String(positionById.get(a.id) ?? "")}
	                      onChange={(e) => setPosDraft((p) => ({ ...p, [a.id]: e.target.value }))}
	                      onKeyDown={(e) => {
	                        if (e.key === "Enter") e.currentTarget.blur();
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
	                        if (raw === undefined) return;
	
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
	
	                  <div className="flex items-center overflow-hidden rounded-lg border border-white/10">
	                    <button
	                      type="button"
	                      className="p-2 hover:bg-white/10"
	                      title="Выше"
	                      onClick={(e) => {
	                        e.preventDefault();
	                        e.stopPropagation();
	                        swapByIndex(idx, idx - 1);
	                      }}
	                    >
	                      <ArrowUp size={16} />
	                    </button>
	                    <button
	                      type="button"
	                      className="p-2 border-l border-white/10 hover:bg-white/10"
	                      title="Ниже"
	                      onClick={(e) => {
	                        e.preventDefault();
	                        e.stopPropagation();
	                        swapByIndex(idx, idx + 1);
	                      }}
	                    >
	                      <ArrowDown size={16} />
	                    </button>
	                  </div>
	                </div>
              </IfEditor>
            ) : null;

          const CardBody = (
            <div className="flex items-start justify-between gap-4 sm:pr-28">
              <div className="min-w-0 grow">
                <div className="flex items-center gap-2">
                  <div
                    className={
                      "text-lg font-semibold truncate " +
                      (solved ? "opacity-70" : "")
                    }
                    title={a.title}
                  >
                    {a.title}
                  </div>
                  {solved && (
                    <span className="inline-flex items-center gap-1 rounded-xl border border-white/15 bg-white/5 px-2 py-0.5 text-[rgb(var(--text))] text-xs opacity-70">
                      <CheckCircle2 size={14} />
                      Решено
                    </span>
                  )}
                </div>

                {a.description && (
                  <p className="mt-1 line-clamp-3 text-sm leading-6 text-neutral-500 sm:line-clamp-2">
                    {previewAssignmentDescription(a.description)}
                  </p>
                )}
                {a.tags && (
                  <div className="mt-2 text-xs text-neutral-400">{a.tags}</div>
                )}
              </div>
            </div>
          );

          const CardBase = (
            <Card
              className={
                "transition hover:shadow-lg " +
                (solved ? "opacity-60 hover:opacity-90" : "")
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
                "relative transition hover:shadow-lg " +
                (solved ? "opacity-60 hover:opacity-90" : "")
              }
            >
              {EditorToolbar}
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
        <div className="card-muted p-8 text-center text-neutral-500 mt-6">
          Пока заданий нет. Создайте первое ✨
        </div>
      )}
    </Layout>
  );
}
