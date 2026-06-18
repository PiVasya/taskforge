import React, { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useParams, Link, useSearchParams } from "react-router-dom";

import Layout from "../components/Layout";
import { Card, Button, Input, Textarea, Badge } from "../components/ui";

import { getCourse } from "../api/courses";
import { getApiErrorMessage } from "../api/http";

import {
  getAssignmentsByCourse,
  createAssignment,
  importAssignmentsFromJson,
  exportAssignmentsToJson,
  updateAssignmentSort,
  moveAssignmentAfter,
} from "../api/assignments";
import { Plus, Layers, CheckCircle2, FileJson, Upload, X, Copy, Sparkles, Download } from "lucide-react";
import IfEditor from "../components/IfEditor";
import { useNotify } from "../components/notify/NotifyProvider";
import { handleApiError } from "../utils/handleApiError";
import { notifyOnce } from "../utils/notifyOnce";

function previewAssignmentTitle(value, fallback = 'Без названия') {
  const text = String(value || '')
    .replace(/<[^>]*>/g, ' ')
    .replace(/&nbsp;/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  return text || fallback;
}

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

function isAssignmentSolved(item) {
  return Boolean(item?.solvedByCurrentUser || item?.isSolved || item?.progressStatus === "solved");
}

const SORT_OPTIONS = [
  { v: "default", label: "Стандартный" },
  { v: "title_asc", label: "A → Я" },
  { v: "title_desc", label: "Я → A" },
  { v: "created_desc", label: "Сначала новые" },
  { v: "created_asc", label: "Сначала старые" },
];

const CREATE_OPTIONS = [
  {
    type: "code-test",
    title: "Code-test",
    subtitle: "Задача с запуском кода и stdin/stdout тестами",
    hint: "Алгоритмы, строки, массивы, структуры данных.",
  },
  {
    type: "test",
    title: "Тест",
    subtitle: "Вопросы A/B/C/D, несколько вариантов, текстовые ответы",
    hint: "Теория, быстрые проверки, ЦТ-подобные вопросы.",
  },
  {
    type: "image-test",
    title: "Image-test",
    subtitle: "Код рисует картинку, система сравнивает результат с эталоном",
    hint: "Turtle, GraphABC, matplotlib, простая графика.",
  },
  {
    type: "math",
    title: "Math",
    subtitle: "Блоки с числами, формулами, порядком и сопоставлением",
    hint: "Пошаговые задания, формулы, соответствия.",
  },
];

const JSON_IMPORT_EXAMPLE_OBJECT = {
  schemaVersion: 1,
  format: "taskforge-course-assignment-import",
  authoringNotes: [
    "Корневой объект может содержать assignments/items/tasks или быть обычным массивом заданий.",
    "Каждый элемент массива станет отдельным заданием курса.",
    "Типы можно смешивать в одном файле: code-test, test, image-test, math.",
    "Для image-test вместо expectedImageKey можно передать expectedImageBase64 с data:image/png;base64,...; сервер переложит картинку в файловое хранилище.",
    "Описание можно передавать plain text или HTML; потом его можно красиво отредактировать в визуальном редакторе.",
    "Заготовку кода для ученика можно передавать через starterCode, templateCode или initialCode; поле также редактируется в обычном редакторе задания.",
    "Если передать id существующего задания из этого курса, импорт обновит это задание вместо создания нового."
  ],
  assignments: [
    {
      type: "code-test",
      title: "Сумма двух чисел",
      description: "Напишите программу, которая считывает два целых числа и выводит их сумму. Ввод: два числа через пробел. Вывод: одно число.",
      language: "cpp",
      allowedLanguages: ["cpp", "python", "csharp", "javascript", "pascal", "java"],
      difficulty: 1,
      rating: 1,
      tags: ["ОАИП", "ввод-вывод", "арифметика"],
      starterCode: "#include <iostream>\nusing namespace std;\n\nint main() {\n    long long a, b;\n    cin >> a >> b;\n    cout << a + b;\n    return 0;\n}\n",
      codeForbiddenCalls: ["system", "exec", "fork"],
      codeRequiredCalls: [],
      testCases: [
        { input: "2 4", expectedOutput: "6", isHidden: false },
        { input: "-5 12", expectedOutput: "7", isHidden: false },
        { input: "1000000000 1000000000", expectedOutput: "2000000000", isHidden: true }
      ]
    },
    {
      type: "code-test",
      title: "Количество слов в строке",
      description: "Считайте строку и выведите количество слов. Словом считается непустая последовательность символов, отделённая пробелами.",
      language: "python",
      allowedLanguages: ["python", "cpp", "csharp", "javascript"],
      difficulty: 1,
      rating: 2,
      tags: "строки, split, базовый ввод",
      starterCode: "s = input()\nprint(len(s.split()))\n",
      testCases: [
        { input: "hello world", expectedOutput: "2", isHidden: false },
        { input: "  one   two three  ", expectedOutput: "3", isHidden: false },
        { input: "     ", expectedOutput: "0", isHidden: true }
      ]
    },
    {
      type: "test",
      title: "Мини-тест по JSON и типам данных",
      description: "Ответьте на вопросы. В текстовых ответах лишние пробелы можно не учитывать.",
      difficulty: 1,
      rating: 3,
      tags: ["теория", "json", "форматы данных"],
      tests: {
        settings: {
          maxAttempts: 2,
          passPercent: 70,
          shuffleQuestions: true,
          shuffleAnswers: true,
          allowReview: true,
          attemptTimeLimitsSeconds: [null, 600]
        },
        questions: [
          {
            id: "00000000-0000-0000-0000-000000000000",
            order: 0,
            type: "single-choice",
            prompt: "Какой тип данных JSON используется для true/false?",
            options: [
              { key: "a", text: "string" },
              { key: "b", text: "boolean" },
              { key: "c", text: "array" },
              { key: "d", text: "number" }
            ],
            correctOptionKeys: ["b"],
            acceptedAnswers: [],
            caseSensitive: false,
            trim: true
          },
          {
            id: "00000000-0000-0000-0000-000000000000",
            order: 1,
            type: "multi-choice",
            prompt: "Какие структуры верхнего уровня допустимы в JSON?",
            options: [
              { key: "a", text: "object" },
              { key: "b", text: "array" },
              { key: "c", text: "function" },
              { key: "d", text: "class" }
            ],
            correctOptionKeys: ["a", "b"],
            acceptedAnswers: [],
            caseSensitive: false,
            trim: true
          },
          {
            id: "00000000-0000-0000-0000-000000000000",
            order: 2,
            type: "text",
            prompt: "Напишите расширение файла JSON без точки.",
            options: [],
            correctOptionKeys: [],
            acceptedAnswers: ["json", "JSON"],
            caseSensitive: false,
            trim: true
          }
        ]
      }
    },
    {
      type: "math",
      title: "Линейное уравнение и соответствия",
      description: "Решите несколько коротких математических блоков.",
      difficulty: 2,
      rating: 4,
      tags: "математика, уравнения, соответствия",
      tests: {
        settings: {
          maxAttempts: 2,
          passPercent: 75,
          shuffleBlocks: false,
          allowReview: true,
          attemptTimeLimitsSeconds: [null]
        },
        blocks: [
          {
            id: "00000000-0000-0000-0000-000000000000",
            order: 0,
            kind: "info",
            prompt: "Дано уравнение 2x + 6 = 14. Найдите x.",
            promptContentJson: "",
            score: 0,
            isRequired: true,
            options: [],
            correctOptionKeys: [],
            acceptedAnswers: [],
            caseSensitive: false,
            trim: true,
            numericTolerance: 0,
            orderItems: [],
            matchLeftItems: [],
            matchRightItems: [],
            matchPairs: []
          },
          {
            id: "00000000-0000-0000-0000-000000000000",
            order: 1,
            kind: "number",
            prompt: "Введите значение x.",
            promptContentJson: "",
            score: 2,
            isRequired: true,
            options: [],
            correctOptionKeys: [],
            acceptedAnswers: ["4"],
            caseSensitive: false,
            trim: true,
            numericTolerance: 0,
            orderItems: [],
            matchLeftItems: [],
            matchRightItems: [],
            matchPairs: []
          },
          {
            id: "00000000-0000-0000-0000-000000000000",
            order: 2,
            kind: "match",
            prompt: "Сопоставьте выражение и значение.",
            promptContentJson: "",
            score: 3,
            isRequired: true,
            options: [],
            correctOptionKeys: [],
            acceptedAnswers: [],
            caseSensitive: false,
            trim: true,
            numericTolerance: 0,
            orderItems: [],
            matchLeftItems: [
              { key: "l1", text: "2 + 3" },
              { key: "l2", text: "3 * 4" },
              { key: "l3", text: "10 - 7" }
            ],
            matchRightItems: [
              { key: "r1", text: "5" },
              { key: "r2", text: "12" },
              { key: "r3", text: "3" }
            ],
            matchPairs: [
              { leftKey: "l1", rightKey: "r1" },
              { leftKey: "l2", rightKey: "r2" },
              { leftKey: "l3", rightKey: "r3" }
            ]
          }
        ]
      }
    },
    {
      type: "image-test",
      title: "Нарисовать красную диагональ",
      description: "Программа должна построить изображение 200x200 и провести диагональ из левого верхнего угла в правый нижний. Для реального задания добавьте expectedImageBase64 или загрузите эталон в редакторе после импорта.",
      language: "python",
      allowedLanguages: ["python", "pascal", "cpp"],
      difficulty: 2,
      rating: 5,
      tags: ["графика", "image-test", "turtle"],
      starterCode: "import turtle\n\nt = turtle.Turtle()\nt.color('red')\nt.goto(100, -100)\nturtle.done()\n",
      imageTestSimilarityThreshold: 90,
      testCases: [
        {
          input: "",
          expectedOutput: "",
          threshold: 90,
          isHidden: false,
          expectedImageBase64: "",
          expectedImageContentType: "image/png",
          expectedImageFileName: "diagonal-reference.png",
          authoringHint: "Замените expectedImageBase64 на data:image/png;base64,... или загрузите эталон в редакторе."
        }
      ]
    }
  ]
};

const JSON_IMPORT_EXAMPLE = JSON.stringify(JSON_IMPORT_EXAMPLE_OBJECT, null, 2);

function buildDefaultAssignmentPayload(type, sort) {
  const normalized = type || "code-test";
  const base = {
    title: "Новое задание",
    description: "Опишите постановку задачи…",
    type: normalized,
    difficulty: 1,
    rating: 1,
    tags: "ОАИП",
    sort,
  };

  if (normalized === "code-test") {
    return {
      ...base,
      language: "cpp",
      allowedLanguages: ["cpp", "python", "csharp", "javascript", "pascal", "java"],
      starterCode: "",
      testCases: [{ input: "2 4", expectedOutput: "6", isHidden: false }],
    };
  }

  if (normalized === "image-test") {
    return {
      ...base,
      language: "python",
      allowedLanguages: ["python", "pascal", "cpp"],
      imageTestSimilarityThreshold: 90,
      testCases: [{ input: "", expectedOutput: "", threshold: 90, isHidden: false }],
    };
  }

  if (normalized === "test") {
    return {
      ...base,
      tests: {
        settings: { maxAttempts: 1, passPercent: 60, shuffleQuestions: true, shuffleAnswers: true, allowReview: true, attemptTimeLimitsSeconds: [] },
        questions: [],
      },
    };
  }

  if (normalized === "math") {
    return {
      ...base,
      tests: {
        settings: { maxAttempts: 1, passPercent: 60, shuffleBlocks: false, allowReview: true, attemptTimeLimitsSeconds: [] },
        blocks: [],
      },
    };
  }

  return base;
}

function summarizeImportPayload(parsed) {
  if (Array.isArray(parsed)) return `${parsed.length} заданий`;
  if (parsed && typeof parsed === "object") {
    const arr = parsed.assignments || parsed.items || parsed.tasks;
    if (Array.isArray(arr)) return `${arr.length} заданий`;
    return "1 задание";
  }
  return "0 заданий";
}

export default function CourseAssignmentsPage() {
  const { courseId } = useParams();
  const nav = useNavigate();
  const [params, setParams] = useSearchParams();
  const notify = useNotify();
  const [items, setItems] = useState([]);
  const [course, setCourse] = useState(null);
  const [courseCanEdit, setCourseCanEdit] = useState(true);
  const [q, setQ] = useState("");
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");


  const [createDialogOpen, setCreateDialogOpen] = useState(false);
  const [createBusyType, setCreateBusyType] = useState("");
  const [jsonImportText, setJsonImportText] = useState(JSON_IMPORT_EXAMPLE);
  const [jsonImportBusy, setJsonImportBusy] = useState(false);
  const [jsonExportBusy, setJsonExportBusy] = useState(false);
  const [jsonImportPreview, setJsonImportPreview] = useState("пример: 5 заданий");
  const [draggedAssignmentId, setDraggedAssignmentId] = useState(null);
  const [dragOverAssignmentId, setDragOverAssignmentId] = useState(null);
  const dragStartedRef = useRef(false);

  const sortMode = params.get("sort") || "default";

  const reloadAssignments = async (silent = false) => {
    try {
      if (!silent) setLoading(true);
      setErr("");
      const data = await getAssignmentsByCourse(courseId);
      const norm = (data || []).map((x, i) => ({
        ...x,
        sort: typeof x.sort === "number" ? x.sort : i,
      }));
      setItems(norm);
      return norm;
    } catch (e) {
      const message = getApiErrorMessage(e, "Не удалось загрузить задания");
      setErr(message);
      throw e;
    } finally {
      if (!silent) setLoading(false);
    }
  };

  useEffect(() => {
    reloadAssignments(false).catch(() => {});
  }, [courseId]);

  useEffect(() => {
    (async () => {
      try {
        const c = await getCourse(courseId);
        setCourse(c || null);
        if (typeof c?.canEdit === 'boolean') setCourseCanEdit(!!c.canEdit);
      } catch {
        
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

  
  
  const orderedAll = useMemo(() => {
    const bySort = (a, b) => (a.sort ?? 0) - (b.sort ?? 0);
    return [...(items || [])].sort(bySort);
  }, [items]);

  const positionById = useMemo(() => {
    const m = new Map();
    orderedAll.forEach((x, idx) => m.set(x.id, idx + 1));
    return m;
  }, [orderedAll]);

  
  
  
  const canEdit = useMemo(() => {
    if (!items || items.length === 0) return true;
    const any = items.find((x) => typeof x?.canEdit === "boolean");
    return any ? !!any.canEdit : true;
  }, [items]);

  const courseProgress = useMemo(() => {
    const total = (items || []).length;
    const solved = (items || []).filter(isAssignmentSolved).length;
    const percent = total > 0 ? Math.round((solved / total) * 100) : 0;
    return { total, solved, percent, isComplete: total > 0 && solved === total };
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

    const newSort = new Map();
    nextOrder.forEach((x, idx) => newSort.set(x.id, idx));

    
    setItems((prev) =>
      prev.map((x) => (newSort.has(x.id) ? { ...x, sort: newSort.get(x.id) } : x))
    );

    try {
      const afterAssignmentId = newIndex > 0 ? nextOrder[newIndex - 1]?.id : null;
      await moveAssignmentAfter(assignmentId, afterAssignmentId || null);
      notify.success("Позиция обновлена");
    } catch (e) {
      
      notify.error("Не удалось изменить позицию");
      
      try {
        const list = await getAssignmentsByCourse(courseId);
        setItems(Array.isArray(list) ? list : []);
      } catch {
        
      }
    }
  };

  const handleDropOnAssignment = async (targetId, sourceFromEvent) => {
    const sourceId = sourceFromEvent || draggedAssignmentId;
    setDraggedAssignmentId(null);
    setDragOverAssignmentId(null);
    if (!sourceId || !targetId || sourceId === targetId) return;
    if (sortMode !== "default") {
      notify.info("Перетаскивание доступно только в стандартной сортировке");
      return;
    }
    const source = orderedAll.find((x) => x.id === sourceId);
    const targetPos = positionById.get(targetId);
    if (!source || !targetPos) return;
    if (source.canEdit === false) {
      notify.error("Недостаточно прав");
      return;
    }
    await moveToPosition(sourceId, targetPos);
  };

  const ensureCanManageAssignments = (actionText = "изменять задания") => {
    if (items.length > 0 && items[0].canEdit === false) {
      notifyOnce("no-edit-course", () =>
        notify.warn(`Вы не владелец курса — ${actionText} нельзя`)
      );
      return false;
    }
    if (!courseCanEdit) {
      notify.warn(`Вы не владелец курса — ${actionText} нельзя`);
      return false;
    }
    return true;
  };

  const handleCreateType = async (type) => {
    if (!ensureCanManageAssignments("создавать задания")) return;
    setCreateBusyType(type);
    try {
      const payload = buildDefaultAssignmentPayload(type, items.length);
      const res = await createAssignment(courseId, payload);
      const id = res && res.id;
      setCreateDialogOpen(false);
      notify.success("Задание создано");
      if (id) nav(`/assignment/${id}/edit`);
    } catch (e) {
      if (e?.response?.status === 403) {
        notifyOnce("no-edit-course", () =>
          notify.error(getApiErrorMessage(e, "Создание запрещено"))
        );
        return;
      }
      handleApiError(e, notify, "Не удалось создать задание");
    } finally {
      setCreateBusyType("");
    }
  };

  const handleJsonImportTextChange = (value) => {
    setJsonImportText(value);
    try {
      const parsed = JSON.parse(value);
      setJsonImportPreview(summarizeImportPayload(parsed));
    } catch {
      setJsonImportPreview("JSON пока не читается");
    }
  };

  const handleJsonFile = async (file) => {
    if (!file) return;
    try {
      const text = await file.text();
      handleJsonImportTextChange(text);
      notify.info(`JSON загружен: ${file.name}`);
    } catch (e) {
      notify.error("Не удалось прочитать JSON-файл");
    }
  };

  const handleImportJson = async () => {
    if (!ensureCanManageAssignments("импортировать JSON")) return;
    let parsed;
    try {
      parsed = JSON.parse(jsonImportText);
    } catch (e) {
      notify.error(`JSON не читается: ${e.message}`);
      return;
    }

    setJsonImportBusy(true);
    try {
      const res = await importAssignmentsFromJson(courseId, parsed);
      const changed = Array.isArray(res?.assignments) ? res.assignments : [];
      await reloadAssignments(true);
      setCreateDialogOpen(false);
      const created = res?.createdCount ?? 0;
      const updated = res?.updatedCount ?? 0;
      notify.success(`Импорт завершён: создано ${created}, обновлено ${updated}`);
      if ((created + updated) === 1 && changed[0]?.id) {
        nav(`/assignment/${changed[0].id}/edit`);
      }
    } catch (e) {
      handleApiError(e, notify, "Не удалось импортировать JSON");
    } finally {
      setJsonImportBusy(false);
    }
  };

  const handleExportJson = async () => {
    if (!ensureCanManageAssignments("экспортировать JSON")) return;
    setJsonExportBusy(true);
    try {
      const data = await exportAssignmentsToJson(courseId);
      const text = JSON.stringify(data, null, 2);
      const safeTitle = (course?.title || "course")
        .toLowerCase()
        .replace(/[^a-zа-яё0-9]+/gi, "-")
        .replace(/^-+|-+$/g, "") || "course";
      const blob = new Blob([text], { type: "application/json;charset=utf-8" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `taskforge-${safeTitle}-assignments.json`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      handleJsonImportTextChange(text);
      notify.success("JSON экспортирован и загружен в редактор импорта");
    } catch (e) {
      handleApiError(e, notify, "Не удалось экспортировать JSON");
    } finally {
      setJsonExportBusy(false);
    }
  };

  return (
    <Layout>
      <div className="page-hero-card mb-6 rounded-[28px] p-5 sm:p-6">
      <div className="flex flex-col gap-5 xl:flex-row xl:items-start xl:justify-between">
        <div className="min-w-0 flex-1">
          <div className="flex min-w-0 flex-col gap-3 lg:flex-row lg:items-center">
            <div className="flex min-w-0 items-center gap-2 sm:gap-3">
              <Button variant="outline" title="Вернуться к курсам" className="shrink-0" onClick={() => nav("/courses")}>
                ← Курсы
              </Button>
              <h1 className="min-w-0 text-xl font-semibold leading-tight sm:text-2xl flex items-center gap-2 flex-wrap">
                <Layers size={22} className="shrink-0" /> <span className="break-words">{course?.title || "Задания курса"}</span>
              </h1>
            </div>
            <div className="w-full max-w-[420px] rounded-2xl border border-[rgba(var(--border)/0.65)] bg-[rgba(var(--muted)/0.18)] px-4 py-3 lg:ml-2 lg:max-w-[360px]">
              <div className="mb-2 text-xs font-medium text-neutral-500">{courseProgress.solved}/{courseProgress.total}</div>
              <div className="h-2 overflow-hidden rounded-full bg-neutral-200/70 dark:bg-white/10">
                <div
                  className="h-full rounded-full bg-[rgb(var(--accent))] transition-all duration-500"
                  style={{ width: `${courseProgress.total > 0 ? courseProgress.percent : 0}%` }}
                />
              </div>
            </div>
          </div>
          {course?.description ? (
            <p className="mt-4 max-w-3xl text-sm leading-6 text-neutral-500">{course.description}</p>
          ) : null}
        </div>

        <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 xl:flex xl:flex-wrap xl:items-center xl:justify-end xl:gap-3">
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
                <Button variant="outline" className="w-full sm:w-auto" onClick={handleExportJson} disabled={jsonExportBusy}>
                  <Download size={16} /> {jsonExportBusy ? "Экспортирую…" : "Экспорт JSON"}
                </Button>
                <Button className="w-full sm:w-auto" onClick={() => setCreateDialogOpen(true)}>
                  <Plus size={16} /> Создать
                </Button>
              </>
            ) : null}
          </IfEditor>
        </div>
      </div>
      </div>

      {createDialogOpen && (
        <div className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/55 px-3 py-6 sm:px-6" onMouseDown={(e) => { if (e.target === e.currentTarget && !jsonImportBusy && !createBusyType) setCreateDialogOpen(false); }}>
          <Card className="w-full max-w-6xl rounded-[28px] border border-[rgba(var(--border)/0.8)] bg-[rgb(var(--card))] p-4 shadow-2xl sm:p-6">
            <div className="flex flex-col gap-3 border-b border-[rgba(var(--border)/0.65)] pb-4 sm:flex-row sm:items-start sm:justify-between">
              <div>
                <div className="flex items-center gap-2 text-xl font-semibold">
                  <Sparkles size={20} /> Что создаём?
                </div>
                <p className="mt-1 text-sm leading-6 text-neutral-500">
                  Создай черновик вручную или импортируй JSON. Если в JSON есть id уже существующего задания этого курса, оно будет обновлено данными из файла.
                </p>
              </div>
              <Button variant="outline" onClick={() => setCreateDialogOpen(false)} disabled={jsonImportBusy || !!createBusyType} title="Закрыть">
                <X size={16} /> Закрыть
              </Button>
            </div>

            <div className="mt-5 grid gap-4 lg:grid-cols-[0.9fr_1.35fr]">
              <div className="space-y-3">
                {CREATE_OPTIONS.map((o) => (
                  <button
                    key={o.type}
                    type="button"
                    disabled={!!createBusyType || jsonImportBusy}
                    onClick={() => handleCreateType(o.type)}
                    className="w-full rounded-2xl border border-[rgba(var(--border)/0.75)] bg-[rgb(var(--muted))]/35 p-4 text-left transition hover:-translate-y-0.5 hover:border-[rgb(var(--primary))]/70 hover:bg-[rgb(var(--primary))]/10 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    <div className="flex items-center justify-between gap-3">
                      <div className="font-semibold">{o.title}</div>
                      <Badge variant="outline">{o.type}</Badge>
                    </div>
                    <div className="mt-1 text-sm leading-5 text-neutral-500">{o.subtitle}</div>
                    <div className="mt-2 text-xs text-neutral-400">{createBusyType === o.type ? "Создаю…" : o.hint}</div>
                  </button>
                ))}

                <div className="rounded-2xl border border-dashed border-[rgba(var(--border)/0.9)] p-4 text-sm leading-6 text-neutral-500">
                  <div className="flex items-center gap-2 font-semibold text-[rgb(var(--fg))]">
                    <FileJson size={16} /> Из JSON
                  </div>
                  <p className="mt-1">
                    Вставь JSON или загрузи файл. Новые задания создаются, а задания с совпавшим <code>id</code> обновляются.
                  </p>
                </div>
              </div>

              <div className="rounded-2xl border border-[rgba(var(--border)/0.75)] bg-[rgb(var(--muted))]/25 p-4">
                <div className="flex flex-col gap-3 border-b border-[rgba(var(--border)/0.65)] pb-3 xl:flex-row xl:items-center xl:justify-between">
                  <div>
                    <div className="flex items-center gap-2 font-semibold">
                      <FileJson size={18} /> JSON-экспорт и импорт
                    </div>
                    <div className="mt-1 text-xs text-neutral-500">
                      Сейчас в поле: {jsonImportPreview}. Экспорт содержит id, поэтому повторный импорт может обновлять существующие задания.
                    </div>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <Button variant="outline" onClick={handleExportJson} disabled={jsonExportBusy || jsonImportBusy}>
                      <Download size={16} /> {jsonExportBusy ? "Экспортирую…" : "Экспорт"}
                    </Button>
                    <label className="btn-outline cursor-pointer">
                      <Upload size={16} /> Загрузить .json
                      <input
                        type="file"
                        accept="application/json,.json"
                        className="hidden"
                        onChange={(e) => handleJsonFile(e.target.files?.[0])}
                      />
                    </label>
                    <Button
                      variant="outline"
                      onClick={async () => {
                        try {
                          await navigator.clipboard.writeText(jsonImportText);
                          notify.success("JSON скопирован");
                        } catch {
                          notify.warn("Браузер не дал скопировать автоматически");
                        }
                      }}
                    >
                      <Copy size={16} /> Копировать
                    </Button>
                    <Button
                      variant="outline"
                      onClick={() => {
                        try {
                          handleJsonImportTextChange(JSON.stringify(JSON.parse(jsonImportText), null, 2));
                        } catch (e) {
                          notify.error(`Нельзя форматировать: ${e.message}`);
                        }
                      }}
                    >
                      Форматировать
                    </Button>
                    <Button variant="outline" onClick={() => handleJsonImportTextChange(JSON_IMPORT_EXAMPLE)}>
                      Вернуть пример
                    </Button>
                  </div>
                </div>

                <Textarea
                  rows={26}
                  value={jsonImportText}
                  onChange={(e) => handleJsonImportTextChange(e.target.value)}
                  spellCheck={false}
                  className="mt-4 min-h-[520px] font-mono text-xs leading-5"
                />

                <div className="mt-4 flex flex-col gap-3 xl:flex-row xl:items-center xl:justify-between">
                  <div className="text-xs leading-5 text-neutral-500">
                    Поддерживаемые поля: <code>id</code>, <code>title</code>, <code>description</code>, <code>type</code>, <code>language</code>, <code>allowedLanguages</code>, <code>starterCode</code>/<code>templateCode</code>, <code>testCases</code>, <code>tests</code>, <code>codeForbiddenCalls</code>, <code>codeRequiredCalls</code>, <code>difficulty</code>, <code>rating</code>, <code>sort</code>, <code>tags</code>.
                  </div>
                  <Button onClick={handleImportJson} disabled={jsonImportBusy || !!createBusyType}>
                    <FileJson size={16} /> {jsonImportBusy ? "Импортирую…" : "Импортировать JSON"}
                  </Button>
                </div>
              </div>
            </div>
          </Card>
        </div>
      )}

      <Card className="page-search-card mb-6 rounded-[24px] p-3 sm:p-4">
        <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
          <div className="relative flex-1">
            <Input
              placeholder="Поиск по названию или тегам"
              value={q}
              onChange={(e) => setQ(e.target.value)}
            />
          </div>        </div>
      </Card>

      {err && <div className="text-red-500 mb-4">{err}</div>}
      {loading && <div className="text-neutral-500">Загрузка…</div>}

      <div className="auto-fill-grid auto-fill-grid--dense">
        {filtered.map((a, idx) => {
          const solved = isAssignmentSolved(a);
          const title = previewAssignmentTitle(a.title, `Задание ${idx + 1}`);
          const assignmentCanEdit = canEdit && a.canEdit !== false;

          const ViewWrap = ({ children }) => (
            <Link to={`/assignment/${a.id}`} className="block group">
              {children}
            </Link>
          );
          const CardMain = (
            <div className="assignment-card-main min-w-0">
              <div className="assignment-card-heading">
                <div className="assignment-card-kicker">Задание {positionById.get(a.id) ?? idx + 1}</div>
                <div className="flex flex-wrap items-center gap-1.5">
                  {a.isAiDraft && <Badge variant="secondary">AI-черновик</Badge>}
                  {a.isHidden && <Badge variant="outline">скрыто</Badge>}
                  {a.lifecycleStatus && a.lifecycleStatus !== 'published' && <Badge variant="outline">{a.lifecycleStatus}</Badge>}
                  {solved && (
                    <span className="assignment-card-status">
                      <CheckCircle2 size={14} />
                      Решено
                    </span>
                  )}
                </div>
              </div>

              <div className="assignment-card-title-wrap">
                <div
                  className={
                    "assignment-card-title" +
                    (solved ? " opacity-70" : "")
                  }
                  title={title}
                >
                  {title}
                </div>
              </div>

              {a.description && (
                <p className="mt-3 text-sm leading-6 text-neutral-500 line-clamp-3">
                  {previewAssignmentDescription(a.description)}
                </p>
              )}
              {a.tags && (
                <div className="mt-2 text-xs text-neutral-400 break-words">{a.tags}</div>
              )}
            </div>
          );

          const baseCardClass =
            "assignment-card h-full transition hover:shadow-lg hover:-translate-y-0.5 " +
            (solved ? "assignment-card--solved " : "") +
            (draggedAssignmentId === a.id ? "assignment-card--dragging " : "") +
            (dragOverAssignmentId === a.id ? "assignment-card--drop-target " : "");

          const CardBase = (
            <Card className={baseCardClass}>
              {CardMain}
            </Card>
          );

          const EditorCard = (
            <Card
              role="link"
              tabIndex={0}
              draggable={assignmentCanEdit && sortMode === "default"}
              onClick={() => {
                if (dragStartedRef.current) {
                  dragStartedRef.current = false;
                  return;
                }
                nav(`/assignment/${a.id}/edit`);
              }}
              onKeyDown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.preventDefault();
                  nav(`/assignment/${a.id}/edit`);
                }
              }}
              onDragStart={(e) => {
                if (!assignmentCanEdit || sortMode !== "default") {
                  e.preventDefault();
                  return;
                }
                dragStartedRef.current = true;
                e.dataTransfer.effectAllowed = "move";
                e.dataTransfer.setData("text/plain", a.id);
                setDraggedAssignmentId(a.id);
              }}
              onDragEnter={(e) => {
                if (!draggedAssignmentId || draggedAssignmentId === a.id) return;
                e.preventDefault();
                setDragOverAssignmentId(a.id);
              }}
              onDragOver={(e) => {
                if (!draggedAssignmentId || draggedAssignmentId === a.id) return;
                e.preventDefault();
                e.dataTransfer.dropEffect = "move";
              }}
              onDragLeave={() => {
                if (dragOverAssignmentId === a.id) setDragOverAssignmentId(null);
              }}
              onDrop={(e) => {
                e.preventDefault();
                const sourceId = e.dataTransfer.getData("text/plain");
                handleDropOnAssignment(a.id, sourceId);
              }}
              onDragEnd={() => {
                setDraggedAssignmentId(null);
                setDragOverAssignmentId(null);
                setTimeout(() => {
                  dragStartedRef.current = false;
                }, 0);
              }}
              className={baseCardClass + (assignmentCanEdit && sortMode === "default" ? " cursor-move" : " cursor-pointer")}
              title={sortMode === "default" ? "Перетащи карточку, чтобы изменить порядок" : "Открыть редактор задания"}
            >
              {CardMain}
            </Card>
          );

          return (
            <IfEditor key={a.id} otherwise={<ViewWrap>{CardBase}</ViewWrap>}>
              {assignmentCanEdit ? EditorCard : <ViewWrap>{CardBase}</ViewWrap>}
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
