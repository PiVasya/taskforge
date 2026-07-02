import React, { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useParams, Link, useSearchParams } from "react-router-dom";

import Layout from "../components/Layout";
import { Card, Button, Input, Textarea, Badge } from "../components/ui";

import { getCourse, getCourses, createCourse, updateCourseSort, moveCoursePosition } from "../api/courses";
import { getApiErrorMessage } from "../api/http";

import {
  getAssignmentsByCourse,
  getCourseProgressByCourses,
  createAssignment,
  importAssignmentsFromJson,
  exportAssignmentsToJson,
  updateAssignmentSort,
} from "../api/assignments";
import { Plus, Layers, FileJson, Upload, X, Copy, Sparkles, Download, GitCompare, AlertTriangle } from "lucide-react";
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

function normalizeProgressRows(rows) {
  const map = new Map();
  for (const row of Array.isArray(rows) ? rows : []) {
    const id = String(row?.courseId || row?.CourseId || "");
    if (!id) continue;
    const total = Number(row?.total ?? row?.Total ?? 0) || 0;
    const solved = Number(row?.solved ?? row?.Solved ?? 0) || 0;
    const percent = total > 0 ? Math.round((solved / total) * 100) : 0;
    map.set(id, { total, solved, percent, isComplete: total > 0 && solved === total, loading: false });
  }
  return map;
}

function buildChildrenByParent(courses) {
  const map = new Map();
  for (const course of Array.isArray(courses) ? courses : []) {
    const parentKey = String(course?.parentCourseId || "");
    if (!map.has(parentKey)) map.set(parentKey, []);
    map.get(parentKey).push(course);
  }
  return map;
}

function collectCourseSubtreeIds(courseId, childrenByParent, seen = new Set()) {
  const id = String(courseId || "");
  if (!id || seen.has(id)) return [];
  seen.add(id);
  const ids = [id];
  for (const child of childrenByParent.get(id) || []) {
    ids.push(...collectCourseSubtreeIds(child.id, childrenByParent, seen));
  }
  return ids;
}

function sumCourseProgress(courseIds, directProgressByCourseId) {
  let total = 0;
  let solved = 0;
  for (const id of courseIds) {
    const row = directProgressByCourseId.get(String(id));
    total += Number(row?.total || 0);
    solved += Number(row?.solved || 0);
  }
  const percent = total > 0 ? Math.round((solved / total) * 100) : 0;
  return { total, solved, percent, isComplete: total > 0 && solved === total, loading: false };
}

const SORT_OPTIONS = [
  { v: "default", label: "Стандартный" },
  { v: "title_asc", label: "A → Я" },
  { v: "title_desc", label: "Я → A" },
  { v: "created_desc", label: "Сначала новые" },
  { v: "created_asc", label: "Сначала старые" },
];

function courseSortValue(course) {
  return Number.isFinite(Number(course?.sort)) ? Number(course.sort) : 0;
}

function compareCourses(a, b) {
  const bySort = courseSortValue(a) - courseSortValue(b);
  if (bySort !== 0) return bySort;
  return String(a?.title || "").localeCompare(String(b?.title || ""), "ru", { sensitivity: "base" });
}

function contentKey(kind, id) {
  return `${kind}:${id}`;
}

function contentSortValue(item) {
  return Number.isFinite(Number(item?.sort)) ? Number(item.sort) : 0;
}

function contentTitle(item) {
  return item?.kind === "course"
    ? String(item.course?.title || "")
    : previewAssignmentTitle(item.assignment?.title, "");
}

function contentCreatedAt(item) {
  return item?.kind === "course" ? item.course?.createdAt : item.assignment?.createdAt;
}

function compareContentItems(a, b) {
  const bySort = contentSortValue(a) - contentSortValue(b);
  if (bySort !== 0) return bySort;
  if (a.kind !== b.kind) return a.kind === "course" ? -1 : 1;
  return contentTitle(a).localeCompare(contentTitle(b), "ru", { sensitivity: "base" });
}

function makeCourseContentItem(course) {
  return {
    kind: "course",
    key: contentKey("course", course.id),
    id: course.id,
    sort: courseSortValue(course),
    title: course.title || "Вложенный курс",
    description: course.description || "",
    course,
  };
}

function makeAssignmentContentItem(assignment, index = 0) {
  return {
    kind: "assignment",
    key: contentKey("assignment", assignment.id),
    id: assignment.id,
    sort: typeof assignment.sort === "number" ? assignment.sort : index,
    title: previewAssignmentTitle(assignment.title, `Задание ${index + 1}`),
    description: assignment.description || "",
    tags: assignment.tags || "",
    assignment,
  };
}

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

const ZERO_GUID = "00000000-0000-0000-0000-000000000000";

function makeImportExamplePayload(assignments, authoringNotes = []) {
  return {
    schemaVersion: 2,
    format: "taskforge-course-assignment-import",
    authoringNotes: [
      "Корневой объект может содержать assignments/items/tasks или быть обычным массивом заданий.",
      "Каждый элемент массива станет отдельным заданием курса.",
      "Если передать id существующего задания из этого курса, импорт обновит это задание вместо создания нового.",
      "Описание можно передавать plain text или HTML; потом его можно отредактировать в визуальном редакторе.",
      "В новых примерах и экспорте нет дублей: у каждого типа задания свои понятные поля.",
      "Для code-test/image-test тесты редактируются через testCases.",
      "Для test настройки редактируются через testSettings, вопросы — через questions.",
      "Для math настройки редактируются через testSettings, блоки — через blocks.",
      "Редактируй именно эти структурные поля, analyticsSettings и остальные обычные поля задания.",
      ...authoringNotes,
    ],
    editableFields: {
      common: ["id", "type", "title", "description", "language", "allowedLanguages", "tags", "difficulty", "rating", "sort", "starterCode", "isVisible", "analyticsSettings"],
      codeTest: ["testCases", "codeForbiddenCalls", "codeRequiredCalls"],
      imageTest: ["testCases", "imageTestReferenceKey", "imageTestSimilarityThreshold", "codeForbiddenCalls", "codeRequiredCalls"],
      test: ["testSettings", "questions"],
      math: ["testSettings", "blocks"],
    },
    assignments,
  };
}

const CODE_TEST_ASSIGNMENT_EXAMPLE = makeImportExamplePayload(
  [
    {
      type: "code-test",
      title: "Сумма двух чисел",
      description: "Напишите программу, которая считывает два целых числа и выводит их сумму. Ввод: два числа через пробел. Вывод: одно число.",
      language: "cpp",
      allowedLanguages: ["cpp", "python", "csharp", "javascript", "pascal", "java"],
      difficulty: 1,
      rating: 1,
      sort: 0,
      tags: "ОАИП, код, ввод-вывод, арифметика",
      starterCode: "#include <iostream>\nusing namespace std;\n\nint main()\n{\n    // Ваш код здесь\n\n    return 0;\n}\n",
      codeForbiddenCalls: ["system", "exec", "fork"],
      codeRequiredCalls: ["cin", "cout"],
      testCases: [
        { input: "2 4", expectedOutput: "6", isHidden: false },
        { input: "-5 12", expectedOutput: "7", isHidden: false },
        { input: "1000000000 1000000000", expectedOutput: "2000000000", isHidden: true },
      ],
      isVisible: true,
      analyticsSettings: {
        mode: "basic",
        trackOpen: true,
        trackAttempts: true,
        trackTime: true,
        trackLanguage: true,
        trackErrors: true,
        trackEditorChanges: false,
        trackClipboard: false,
        trackFocus: false,
        trackVisibility: false,
        trackFullscreen: false,
        trackCodeSnapshots: false,
        trackRiskScore: false,
        trackLiveActivity: false,
        trackSimilarity: false,
        storeFullCode: false,
        storePasteText: false,
        storeTextSamples: true,
        pasteSampleLimit: 500,
        codeSampleLimit: 1000,
        codeSnapshotIntervalSeconds: 45,
        eventBatchIntervalSeconds: 10,
        retentionDays: 30,
        maxFullCodeLength: 80000,
        maxEventsPerBatch: 120,
      },
    },
  ],
  [
    "code-test — один тип задания для любых языков программирования.",
    "Язык меняется полем language: cpp, python, csharp, javascript, pascal или java.",
    "allowedLanguages задаёт языки, доступные ученику в редакторе.",
    "starterCode меняется под выбранный язык. Для Python это может быть '# Ваш код здесь\n'.",
    "codeRequiredCalls и codeForbiddenCalls опциональны: они нужны только если надо проверить наличие/запрет конкретных вызовов.",
    "В новом JSON для code-test используется только testCases.",
    "isHidden=true скрывает тест от ученика.",
  ],
);

const TEST_ASSIGNMENT_EXAMPLE = makeImportExamplePayload(
  [
    {
      type: "test",
      title: "Мини-тест по JSON",
      description: "Ответьте на вопросы. Есть один выбор, несколько вариантов и краткий текстовый ответ.",
      difficulty: 1,
      rating: 3,
      sort: 0,
      tags: "теория, json, тест",
      testSettings: {
        maxAttempts: 2,
        passPercent: 70,
        shuffleQuestions: true,
        shuffleAnswers: true,
        allowReview: true,
        attemptTimeLimitsSeconds: [null, 600],
      },
      questions: [
          {
            id: ZERO_GUID,
            order: 0,
            type: "single-choice",
            prompt: "Какой тип данных JSON используется для true/false?",
            options: [
              { key: "a", text: "string" },
              { key: "b", text: "boolean" },
              { key: "c", text: "array" },
              { key: "d", text: "number" },
            ],
            correctOptionKeys: ["b"],
            acceptedAnswers: [],
            caseSensitive: false,
            trim: true,
          },
          {
            id: ZERO_GUID,
            order: 1,
            type: "multi-choice",
            prompt: "Какие структуры верхнего уровня допустимы в JSON?",
            options: [
              { key: "a", text: "object" },
              { key: "b", text: "array" },
              { key: "c", text: "function" },
              { key: "d", text: "class" },
            ],
            correctOptionKeys: ["a", "b"],
            acceptedAnswers: [],
            caseSensitive: false,
            trim: true,
          },
          {
            id: ZERO_GUID,
            order: 2,
            type: "text",
            prompt: "Напишите расширение файла JSON без точки.",
            options: [],
            correctOptionKeys: [],
            acceptedAnswers: ["json", "JSON"],
            caseSensitive: false,
            trim: true,
          },
      ],
      isVisible: true,
    },
  ],
  [
    "test хранит настройки в testSettings, а вопросы в questions.",
    "type вопроса: single-choice, multi-choice или text.",
    "Для text-вопроса варианты не нужны: правильные ответы лежат в acceptedAnswers.",
  ],
);

const MATH_ASSIGNMENT_EXAMPLE = makeImportExamplePayload(
  [
    {
      type: "math",
      title: "Линейное уравнение и соответствия",
      description: "Решите несколько коротких математических блоков.",
      difficulty: 2,
      rating: 4,
      sort: 0,
      tags: "математика, уравнения, соответствия",
      testSettings: {
        maxAttempts: 2,
        passPercent: 75,
        shuffleBlocks: false,
        allowReview: true,
        attemptTimeLimitsSeconds: [null],
      },
      blocks: [
          {
            id: ZERO_GUID,
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
            matchPairs: [],
          },
          {
            id: ZERO_GUID,
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
            matchPairs: [],
          },
          {
            id: ZERO_GUID,
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
              { key: "l3", text: "10 - 7" },
            ],
            matchRightItems: [
              { key: "r1", text: "5" },
              { key: "r2", text: "12" },
              { key: "r3", text: "3" },
            ],
            matchPairs: [
              { leftKey: "l1", rightKey: "r1" },
              { leftKey: "l2", rightKey: "r2" },
              { leftKey: "l3", rightKey: "r3" },
            ],
          },
      ],
      isVisible: true,
    },
  ],
  [
    "math хранит настройки в testSettings, а блоки в blocks.",
    "kind может быть info, number, text, single-choice, multi-choice, order или match.",
    "Для match используются matchLeftItems, matchRightItems и matchPairs.",
  ],
);

const IMAGE_TEST_ASSIGNMENT_EXAMPLE = makeImportExamplePayload(
  [
    {
      type: "image-test",
      title: "Нарисовать диагональ",
      description: "Программа должна построить изображение 200x200 и провести диагональ из левого верхнего угла в правый нижний. После импорта можно загрузить эталон в редакторе задания.",
      language: "python",
      allowedLanguages: ["python", "pascal", "cpp"],
      difficulty: 2,
      rating: 5,
      sort: 0,
      tags: "графика, image-test, turtle",
      starterCode: "import turtle\n\nt = turtle.Turtle()\nt.color('red')\nt.goto(100, -100)\nturtle.done()\n",
      imageTestSimilarityThreshold: 90,
      testCases: [
        {
          input: "",
          expectedOutput: "",
          isHidden: false,
          expectedImageBase64: "",
          expectedImageContentType: "image/png",
          expectedImageFileName: "diagonal-reference.png",
          authoringHint: "Замените expectedImageBase64 на data:image/png;base64,... или загрузите эталон в редакторе после импорта.",
        },
      ],
      isVisible: true,
    },
  ],
  [
    "image-test использует те же testCases, но дополнительно нужен эталон изображения.",
    "expectedImageBase64 можно оставить пустым и загрузить эталон через редактор после импорта.",
  ],
);

const MIXED_ASSIGNMENT_EXAMPLE = makeImportExamplePayload(
  [
    CODE_TEST_ASSIGNMENT_EXAMPLE.assignments[0],
    TEST_ASSIGNMENT_EXAMPLE.assignments[0],
    MATH_ASSIGNMENT_EXAMPLE.assignments[0],
    IMAGE_TEST_ASSIGNMENT_EXAMPLE.assignments[0],
  ],
  [
    "Это смешанный пример: сразу code-test, test, math и image-test в одном файле.",
    "Можно удалить лишние элементы из assignments и оставить только нужные задания.",
  ],
);

const JSON_IMPORT_EXAMPLES = [
  {
    key: "code-test",
    title: "Код-тест",
    type: "code-test",
    description: "Один пример для любого языка: меняются language, allowedLanguages, starterCode и testCases.",
    payload: CODE_TEST_ASSIGNMENT_EXAMPLE,
  },
  {
    key: "test",
    title: "Тест",
    type: "test",
    description: "single-choice, multi-choice и текстовый ответ внутри одного теста.",
    payload: TEST_ASSIGNMENT_EXAMPLE,
  },
  {
    key: "math",
    title: "Math",
    type: "math",
    description: "Информационный блок, числовой ответ и сопоставление.",
    payload: MATH_ASSIGNMENT_EXAMPLE,
  },
  {
    key: "image-test",
    title: "Image-test",
    type: "image-test",
    description: "Графическая задача с порогом похожести и местом для эталона.",
    payload: IMAGE_TEST_ASSIGNMENT_EXAMPLE,
  },
  {
    key: "mixed",
    title: "Смешанный файл",
    type: "mixed",
    description: "Все основные виды заданий сразу, как большой пример для нейронки.",
    payload: MIXED_ASSIGNMENT_EXAMPLE,
  },
];

const JSON_IMPORT_EXAMPLE = JSON.stringify(MIXED_ASSIGNMENT_EXAMPLE, null, 2);

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
      testCases: [{ input: "", expectedOutput: "", isHidden: false }],
    };
  }

  if (normalized === "test") {
    return {
      ...base,
      testSettings: { maxAttempts: 1, passPercent: 60, shuffleQuestions: true, shuffleAnswers: true, allowReview: true, attemptTimeLimitsSeconds: [] },
      questions: [],
    };
  }

  if (normalized === "math") {
    return {
      ...base,
      testSettings: { maxAttempts: 1, passPercent: 60, shuffleBlocks: false, allowReview: true, attemptTimeLimitsSeconds: [] },
      blocks: [],
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


function getImportAssignments(parsed) {
  if (Array.isArray(parsed)) return parsed.filter(Boolean);
  if (parsed && typeof parsed === "object") {
    const arr = parsed.assignments || parsed.items || parsed.tasks;
    if (Array.isArray(arr)) return arr.filter(Boolean);
    return [parsed];
  }
  return [];
}

function normalizeImportId(value) {
  const id = String(value || "").trim();
  return id && id !== ZERO_GUID ? id : "";
}

function tryParseJsonString(value) {
  if (typeof value !== "string") return value;
  const text = value.trim();
  if (!text) return "";
  if (!text.startsWith("{") && !text.startsWith("[")) return value;
  try { return JSON.parse(text); } catch { return value; }
}

function normalizeTagsValue(value) {
  if (Array.isArray(value)) return value.map((x) => String(x).trim()).filter(Boolean).join(", ");
  return value ?? "";
}

function normalizeForDiff(value) {
  const parsed = tryParseJsonString(value);
  if (parsed === undefined) return null;
  if (parsed === null) return null;
  if (Array.isArray(parsed)) return parsed.map(normalizeForDiff);
  if (typeof parsed === "object") {
    return Object.keys(parsed).sort().reduce((acc, key) => {
      acc[key] = normalizeForDiff(parsed[key]);
      return acc;
    }, {});
  }
  return parsed;
}

function sameImportValue(a, b) {
  return JSON.stringify(normalizeForDiff(a)) === JSON.stringify(normalizeForDiff(b));
}

function shortImportValue(value) {
  const normalized = normalizeForDiff(value);
  if (normalized === null || normalized === undefined || normalized === "") return "—";
  const text = typeof normalized === "string" ? normalized : JSON.stringify(normalized, null, 2);
  return text.length > 420 ? `${text.slice(0, 420)}…` : text;
}

function readImportVisibility(x) {
  if (!x || typeof x !== "object") return undefined;
  if (x.isVisible !== undefined) return x.isVisible;
  if (x.visible !== undefined) return x.visible;
  if (x.isHidden !== undefined) return !x.isHidden;
  if (x.hidden !== undefined) return !x.hidden;
  return undefined;
}

const IMPORT_DIFF_FIELDS = [
  { key: "title", label: "Название", read: (x) => x?.title ?? x?.assignmentTitle },
  { key: "type", label: "Тип", read: (x) => x?.type },
  { key: "description", label: "Описание", read: (x) => x?.description },
  { key: "language", label: "Язык", read: (x) => x?.language },
  { key: "allowedLanguages", label: "Доступные языки", read: (x) => x?.allowedLanguages },
  { key: "tags", label: "Теги", read: (x) => normalizeTagsValue(x?.tags) },
  { key: "difficulty", label: "Сложность", read: (x) => x?.difficulty },
  { key: "rating", label: "Рейтинг", read: (x) => x?.rating },
  { key: "sort", label: "Порядок", read: (x) => x?.sort },
  { key: "starterCode", label: "Стартовый код", read: (x) => x?.starterCode ?? x?.templateCode },
  { key: "testCases", label: "Тест-кейсы кода/картинки", read: (x) => ["code-test", "image-test"].includes(x?.type) ? (x?.testCases ?? x?.cases ?? x?.tests) : undefined },
  { key: "testSettings", label: "Настройки попыток", read: (x) => ["test", "math"].includes(x?.type) ? (x?.testSettings ?? x?.settings ?? x?.mathSettings ?? x?.quizSettings ?? x?.tests?.settings ?? x?.testSpec?.settings ?? x?.mathSpec?.settings) : undefined },
  { key: "questions", label: "Вопросы теста", read: (x) => x?.type === "test" ? (x?.questions ?? x?.tests?.questions ?? x?.testSpec?.questions) : undefined },
  { key: "blocks", label: "Math-блоки", read: (x) => x?.type === "math" ? (x?.blocks ?? x?.tests?.blocks ?? x?.mathSpec?.blocks) : undefined },
  { key: "codeRequiredCalls", label: "Обязательные вызовы", read: (x) => x?.codeRequiredCalls },
  { key: "codeForbiddenCalls", label: "Запрещённые вызовы", read: (x) => x?.codeForbiddenCalls },
  { key: "isVisible", label: "Видимость", read: readImportVisibility },
  { key: "imageTestReferenceKey", label: "Эталон картинки", read: (x) => x?.imageTestReferenceKey },
  { key: "imageTestSimilarityThreshold", label: "Порог картинки", read: (x) => x?.imageTestSimilarityThreshold },
  { key: "analyticsSettings", label: "Аналитика", read: (x) => x?.analyticsSettings ?? x?.assignmentAnalyticsSettings ?? x?.analytics },
];

function buildJsonImportDiff(parsed, currentExport) {
  const incoming = getImportAssignments(parsed);
  const current = getImportAssignments(currentExport);
  const byId = new Map(current.map((x) => [String(x.id || x.assignmentId || ""), x]).filter(([id]) => id));
  const currentTitles = new Set(current.map((x) => String(x.title || x.assignmentTitle || "").trim().toLowerCase()).filter(Boolean));

  const rows = incoming.map((item, index) => {
    const id = normalizeImportId(item?.id ?? item?.assignmentId);
    const existing = id ? byId.get(id) : null;
    const title = previewAssignmentTitle(item?.title, `Импорт #${index + 1}`);
    const duplicateTitle = !existing && title && currentTitles.has(title.trim().toLowerCase());
    const changes = existing
      ? IMPORT_DIFF_FIELDS.map((field) => {
          const before = field.read(existing);
          const after = field.read(item);
          if (after === undefined || sameImportValue(before, after)) return null;
          return { key: field.key, label: field.label, before, after };
        }).filter(Boolean)
      : [];

    return {
      index,
      id,
      title,
      type: item?.type || existing?.type || "assignment",
      action: existing ? (changes.length ? "update" : "unchanged") : "create",
      duplicateTitle,
      changes,
    };
  });

  return {
    total: rows.length,
    createCount: rows.filter((x) => x.action === "create").length,
    updateCount: rows.filter((x) => x.action === "update").length,
    unchangedCount: rows.filter((x) => x.action === "unchanged").length,
    withoutIdCount: rows.filter((x) => !x.id).length,
    duplicateTitleCount: rows.filter((x) => x.duplicateTitle).length,
    rows,
  };
}

export default function CourseAssignmentsPage() {
  const { courseId } = useParams();
  const nav = useNavigate();
  const [params, setParams] = useSearchParams();
  const notify = useNotify();
  const [items, setItems] = useState([]);
  const [course, setCourse] = useState(null);
  const [childCourses, setChildCourses] = useState([]);
  const [allCourses, setAllCourses] = useState([]);
  const [childProgressByCourseId, setChildProgressByCourseId] = useState({});
  const [courseProgressByCourseId, setCourseProgressByCourseId] = useState({});
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
  const [jsonImportDiffOpen, setJsonImportDiffOpen] = useState(false);
  const [jsonImportDiff, setJsonImportDiff] = useState(null);
  const [jsonImportParsed, setJsonImportParsed] = useState(null);
  const [draggedContentKey, setDraggedContentKey] = useState(null);
  const [dragOverContentKey, setDragOverContentKey] = useState(null);
  const [dragOverContentMode, setDragOverContentMode] = useState('before');
  const [extractDropActive, setExtractDropActive] = useState(false);
  const dragStartedRef = useRef(false);

  const sortMode = params.get("sort") || "default";

  useEffect(() => {
    if (!jsonImportDiffOpen) return undefined;

    const previousBodyOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";

    const handleKeyDown = (event) => {
      if (event.key === "Escape" && !jsonImportBusy) {
        setJsonImportDiffOpen(false);
      }
    };

    window.addEventListener("keydown", handleKeyDown);
    return () => {
      document.body.style.overflow = previousBodyOverflow;
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [jsonImportDiffOpen, jsonImportBusy]);

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

  const reloadCourseData = async () => {
    try {
      const [c, coursesPayload] = await Promise.all([
        getCourse(courseId),
        getCourses().catch(() => []),
      ]);
      setCourse(c || null);
      if (typeof c?.canEdit === 'boolean') setCourseCanEdit(!!c.canEdit);
      const allCourses = Array.isArray(coursesPayload?.items) ? coursesPayload.items : coursesPayload;
      const normalizedCourses = Array.isArray(allCourses) ? allCourses : [];
      setAllCourses(normalizedCourses);
      const children = normalizedCourses
        .filter((x) => String(x?.parentCourseId || '') === String(courseId))
        .sort(compareCourses);
      setChildCourses(children);
      return children;
    } catch {
      setAllCourses([]);
      setChildCourses([]);
      return [];
    }
  };

  useEffect(() => {
    reloadAssignments(false).catch(() => {});
    reloadCourseData().catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [courseId]);

  const contentItems = useMemo(() => {
    const courses = (childCourses || []).map(makeCourseContentItem);
    const assignments = (items || []).map((assignment, index) => makeAssignmentContentItem(assignment, index));
    return [...courses, ...assignments];
  }, [childCourses, items]);

  useEffect(() => {
    let cancelled = false;
    const knownCourses = [...(allCourses || [])];
    if (course?.id && !knownCourses.some((x) => String(x?.id || '') === String(course.id))) {
      knownCourses.push(course);
    }

    const childrenByParent = buildChildrenByParent(knownCourses);
    const subtreeIds = collectCourseSubtreeIds(courseId, childrenByParent);
    if (subtreeIds.length === 0) {
      setCourseProgressByCourseId({});
      setChildProgressByCourseId({});
      return () => {
        cancelled = true;
      };
    }

    setCourseProgressByCourseId((prev) => ({
      ...prev,
      [courseId]: prev[courseId] || { loading: true, total: 0, solved: 0, percent: 0, isComplete: false },
    }));
    setChildProgressByCourseId((prev) => {
      const next = { ...prev };
      for (const child of childCourses || []) {
        if (child?.id && !next[child.id]) next[child.id] = { loading: true, total: 0, solved: 0, percent: 0, isComplete: false };
      }
      return next;
    });

    getCourseProgressByCourses(subtreeIds)
      .then((rows) => {
        if (cancelled) return;
        const directProgress = normalizeProgressRows(rows);
        const currentProgress = sumCourseProgress(subtreeIds, directProgress);
        const nextChildProgress = {};
        for (const child of childCourses || []) {
          if (!child?.id) continue;
          const childSubtreeIds = collectCourseSubtreeIds(child.id, childrenByParent);
          nextChildProgress[child.id] = sumCourseProgress(childSubtreeIds, directProgress);
        }
        setCourseProgressByCourseId({ [courseId]: currentProgress });
        setChildProgressByCourseId(nextChildProgress);
      })
      .catch(() => {
        if (cancelled) return;
        const directTotal = (items || []).length;
        const directSolved = (items || []).filter(isAssignmentSolved).length;
        const directPercent = directTotal > 0 ? Math.round((directSolved / directTotal) * 100) : 0;
        const failedCurrent = { total: directTotal, solved: directSolved, percent: directPercent, isComplete: directTotal > 0 && directSolved === directTotal, loading: false, failed: true };
        const failedChildren = {};
        for (const child of childCourses || []) {
          if (child?.id) failedChildren[child.id] = failedCurrent;
        }
        setCourseProgressByCourseId({ [courseId]: failedCurrent });
        setChildProgressByCourseId(failedChildren);
      });

    return () => {
      cancelled = true;
    };
  }, [allCourses, childCourses, course, courseId, items]);

  const filtered = useMemo(() => {
    const query = q.trim().toLowerCase();
    const s = (contentItems || []).filter((x) => {
      if (!query) return true;
      return (
        contentTitle(x).toLowerCase().includes(query) ||
        String(x.description || "").toLowerCase().includes(query) ||
        String(x.tags || "").toLowerCase().includes(query)
      );
    });

    const byTitle = (a, b, dir = 1) =>
      contentTitle(a).localeCompare(contentTitle(b), "ru", { sensitivity: "base" }) * dir;
    const byCreated = (a, b, dir = 1) =>
      ((new Date(contentCreatedAt(a) || 0).getTime()) - (new Date(contentCreatedAt(b) || 0).getTime())) * dir;

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
        return [...s].sort(compareContentItems);
    }
  }, [contentItems, q, sortMode]);

  const orderedAll = useMemo(() => [...(contentItems || [])].sort(compareContentItems), [contentItems]);

  const positionByKey = useMemo(() => {
    const m = new Map();
    orderedAll.forEach((x, idx) => m.set(x.key, idx + 1));
    return m;
  }, [orderedAll]);

  const canEdit = useMemo(() => {
    if (!items || items.length === 0) return true;
    const any = items.find((x) => typeof x?.canEdit === "boolean");
    return any ? !!any.canEdit : true;
  }, [items]);

  const directCourseProgress = useMemo(() => {
    const total = (items || []).length;
    const solved = (items || []).filter(isAssignmentSolved).length;
    const percent = total > 0 ? Math.round((solved / total) * 100) : 0;
    return { total, solved, percent, isComplete: total > 0 && solved === total, loading: false };
  }, [items]);

  const courseProgress = useMemo(() => {
    return courseProgressByCourseId[courseId] || directCourseProgress;
  }, [courseProgressByCourseId, courseId, directCourseProgress]);

  const setSortMode = (mode) => {
    const next = new URLSearchParams(params);
    next.set("sort", mode);
    setParams(next, { replace: true });
  };

  const canReorderContentItem = (item) => {
    if (!courseCanEdit) return false;
    if (item?.kind === "course") return item.course?.canEdit !== false;
    return canEdit && item?.assignment?.canEdit !== false;
  };

  const contentDropModeFromEvent = (event, targetEntry) => {
    const source = orderedAll.find((x) => x.key === draggedContentKey);
    if (source?.kind === "course" && targetEntry?.kind === "course" && source.key !== targetEntry.key) {
      const rect = event.currentTarget.getBoundingClientRect();
      const y = event.clientY - rect.top;
      if (y < rect.height * 0.25) return "before";
      if (y > rect.height * 0.75) return "after";
      return "inside";
    }
    return "before";
  };

  const moveCourseIntoParent = async (sourceKey, parentCourseId) => {
    const source = orderedAll.find((x) => x.key === sourceKey);
    if (!source || source.kind !== "course" || !canReorderContentItem(source)) {
      notify.error("Недостаточно прав");
      return;
    }

    const parentId = parentCourseId || null;
    const siblingsCount = (allCourses || []).filter((x) => String(x?.parentCourseId || '') === String(parentId || '')).length;

    setChildCourses((prev) => prev.filter((x) => String(x.id) !== String(source.id)));
    try {
      await moveCoursePosition(source.id, parentId, siblingsCount + 1);
      notify.success(parentId ? "Курс вложен" : "Курс вынесен на уровень выше");
      await reloadCourseData();
    } catch (e) {
      handleApiError(e, notify, "Не удалось переместить курс");
      await reloadCourseData().catch(() => []);
    }
  };

  const saveMixedOrder = async (nextOrder) => {
    const calls = [];
    nextOrder.forEach((entry, index) => {
      if (entry.sort === index) return;
      if (entry.kind === "course") calls.push(updateCourseSort(entry.id, index));
      else calls.push(updateAssignmentSort(entry.id, index));
    });
    if (calls.length > 0) await Promise.all(calls);
  };

  const moveToPosition = async (contentItemKey, newPos1Based) => {
    if (!courseCanEdit) {
      notify.error("Недостаточно прав");
      return;
    }
    if (sortMode !== "default") {
      notify.info("Изменение позиции доступно только в стандартной сортировке");
      return;
    }

    const n = orderedAll.length;
    let targetPos = parseInt(String(newPos1Based || ""), 10);
    if (!Number.isFinite(targetPos)) return;
    if (targetPos < 1) targetPos = 1;
    if (targetPos > n) targetPos = n;

    const curIndex = orderedAll.findIndex((x) => x.key === contentItemKey);
    if (curIndex < 0) return;
    const source = orderedAll[curIndex];
    if (!canReorderContentItem(source)) {
      notify.error("Недостаточно прав");
      return;
    }

    const newIndex = targetPos - 1;
    if (newIndex === curIndex) return;

    const nextOrder = [...orderedAll];
    const [moved] = nextOrder.splice(curIndex, 1);
    nextOrder.splice(newIndex, 0, moved);

    const newSort = new Map(nextOrder.map((x, idx) => [x.key, idx]));
    setItems((prev) => prev.map((x) => {
      const next = newSort.get(contentKey("assignment", x.id));
      return Number.isFinite(next) ? { ...x, sort: next } : x;
    }));
    setChildCourses((prev) => prev.map((x) => {
      const next = newSort.get(contentKey("course", x.id));
      return Number.isFinite(next) ? { ...x, sort: next } : x;
    }));

    try {
      await saveMixedOrder(nextOrder);
      notify.success("Позиция обновлена");
    } catch (e) {
      handleApiError(e, notify, "Не удалось изменить позицию");
      await Promise.all([
        reloadAssignments(true).catch(() => []),
        reloadCourseData().catch(() => []),
      ]);
    }
  };

  const handleDropOnContentItem = async (targetKey, sourceFromEvent, mode = dragOverContentMode) => {
    const sourceKey = sourceFromEvent || draggedContentKey;
    setDraggedContentKey(null);
    setDragOverContentKey(null);
    setDragOverContentMode('before');
    if (!sourceKey || !targetKey || sourceKey === targetKey) return;
    if (sortMode !== "default") {
      notify.info("Перетаскивание доступно только в стандартной сортировке");
      return;
    }
    const source = orderedAll.find((x) => x.key === sourceKey);
    const target = orderedAll.find((x) => x.key === targetKey);
    const targetPos = positionByKey.get(targetKey);
    if (!source || !target || !targetPos) return;
    if (!canReorderContentItem(source)) {
      notify.error("Недостаточно прав");
      return;
    }
    if (mode === 'inside' && source.kind === 'course' && target.kind === 'course') {
      await moveCourseIntoParent(sourceKey, target.id);
      return;
    }
    await moveToPosition(sourceKey, mode === 'after' ? targetPos + 1 : targetPos);
  };

  const handleDropCourseOneLevelUp = async (event) => {
    event.preventDefault();
    setExtractDropActive(false);
    const sourceKey = event.dataTransfer.getData("text/plain") || draggedContentKey;
    setDraggedContentKey(null);
    setDragOverContentKey(null);
    setDragOverContentMode('before');
    if (!sourceKey) return;
    const source = orderedAll.find((x) => x.key === sourceKey);
    if (!source || source.kind !== 'course') return;
    await moveCourseIntoParent(sourceKey, course?.parentCourseId || null);
  };

  const getDraggedCourseItem = () => orderedAll.find((x) => x.key === draggedContentKey && x.kind === 'course');

  const handleExtractZoneDragOver = (event) => {
    const source = getDraggedCourseItem();
    if (!source || !canReorderContentItem(source) || sortMode !== "default") return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "move";
    setExtractDropActive(true);
  };

  const handleExtractZoneDragLeave = (event) => {
    if (event.currentTarget.contains(event.relatedTarget)) return;
    setExtractDropActive(false);
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
      const payload = buildDefaultAssignmentPayload(type, orderedAll.length);
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

  const handleCreateChildCourse = async () => {
    if (!ensureCanManageAssignments("создавать вложенный курс")) return;
    setCreateBusyType("course");
    try {
      const res = await createCourse({
        title: "Новый вложенный курс",
        description: "Описание курса",
        isPublic: false,
        visibleGroupIds: [],
        ownerIds: [],
        parentCourseId: courseId,
        sort: orderedAll.length,
      });
      const id = res && res.id;
      setCreateDialogOpen(false);
      notify.success("Вложенный курс создан");
      if (id) nav(`/courses/${id}/edit`);
    } catch (e) {
      handleApiError(e, notify, "Не удалось создать вложенный курс");
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

  const exampleToText = (example) => JSON.stringify(example.payload, null, 2);

  const handleUseJsonExample = (example) => {
    handleJsonImportTextChange(exampleToText(example));
    notify.info(`В редактор вставлен пример: ${example.title}`);
  };

  const handleCopyJsonExample = async (example) => {
    try {
      await navigator.clipboard.writeText(exampleToText(example));
      notify.success(`Скопирован пример: ${example.title}`);
    } catch {
      notify.warn("Браузер не дал скопировать автоматически");
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

  const applyJsonImport = async (parsed) => {
    setJsonImportBusy(true);
    try {
      const res = await importAssignmentsFromJson(courseId, parsed);
      const changed = Array.isArray(res?.assignments) ? res.assignments : [];
      await reloadAssignments(true);
      setJsonImportDiffOpen(false);
      setJsonImportDiff(null);
      setJsonImportParsed(null);
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

  const handlePrepareJsonImportDiff = async () => {
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
      const currentExport = await exportAssignmentsToJson(courseId);
      const diff = buildJsonImportDiff(parsed, currentExport);
      setJsonImportParsed(parsed);
      setJsonImportDiff(diff);
      setJsonImportDiffOpen(true);
      if (diff.total === 0) notify.warn("В JSON не найдено заданий для импорта");
    } catch (e) {
      handleApiError(e, notify, "Не удалось подготовить дифф импорта");
    } finally {
      setJsonImportBusy(false);
    }
  };

  const handleApplyPreparedJsonImport = async () => {
    if (!jsonImportParsed) {
      notify.error("Сначала подготовьте дифф импорта");
      return;
    }
    await applyJsonImport(jsonImportParsed);
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
              <Button
                variant="outline"
                title={course?.parentCourseId ? "Вернуться на уровень выше" : "Вернуться к курсам"}
                className="shrink-0"
                onClick={() => nav(course?.parentCourseId ? `/course/${course.parentCourseId}` : "/courses")}
                onDragOver={(e) => {
                  if (!draggedContentKey) return;
                  const source = orderedAll.find((x) => x.key === draggedContentKey);
                  if (source?.kind !== 'course') return;
                  e.preventDefault();
                }}
                onDrop={handleDropCourseOneLevelUp}
              >
                {course?.parentCourseId ? "← Назад" : "← Курсы"}
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


      <IfEditor>
        {courseCanEdit && childCourses.length > 0 && getDraggedCourseItem() ? (
          <div
            className={
              "fixed bottom-5 left-1/2 z-[1000] w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 rounded-2xl border border-dashed px-5 py-4 text-sm shadow-2xl backdrop-blur transition sm:bottom-7 " +
              (extractDropActive
                ? "border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.16)] text-[rgb(var(--accent))]"
                : "border-[rgba(var(--border)/0.9)] bg-[rgba(var(--card)/0.96)] text-neutral-500")
            }
            onDragOver={handleExtractZoneDragOver}
            onDragEnter={handleExtractZoneDragOver}
            onDragLeave={handleExtractZoneDragLeave}
            onDrop={handleDropCourseOneLevelUp}
          >
            <div className="font-medium text-current">
              Вынести курс на уровень выше
            </div>
            <div className="mt-1 text-xs opacity-80">
              Отпусти здесь курс: он переместится {course?.parentCourseId ? "в родительский курс" : "в корень каталога"}.
            </div>
          </div>
        ) : null}
      </IfEditor>

      {jsonImportDiffOpen && jsonImportDiff && (
        <div
          className="fixed inset-0 z-[9999] flex h-[100dvh] items-center justify-center overflow-hidden bg-black/65 p-3 sm:p-5"
          onMouseDown={(e) => { if (e.target === e.currentTarget && !jsonImportBusy) setJsonImportDiffOpen(false); }}
        >
          <Card className="flex max-h-[calc(100dvh-1.5rem)] w-full max-w-6xl flex-col overflow-hidden rounded-[28px] border border-[rgba(var(--border)/0.8)] bg-[rgb(var(--card))] p-0 shadow-2xl sm:max-h-[calc(100dvh-2.5rem)]">
            <div className="shrink-0 border-b border-[rgba(var(--border)/0.65)] bg-[rgb(var(--card))] px-4 py-4 sm:px-6">
              <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                <div>
                  <div className="flex items-center gap-2 text-xl font-semibold">
                    <GitCompare size={20} /> Дифф JSON-импорта
                  </div>
                  <p className="mt-1 text-sm leading-6 text-neutral-500">
                    Проверь, что будет создано или обновлено. Изменения применятся только после кнопки «Применить».
                  </p>
                </div>
                <div className="flex flex-wrap gap-2">
                  <Button variant="outline" onClick={() => setJsonImportDiffOpen(false)} disabled={jsonImportBusy}>
                    <X size={16} /> Назад
                  </Button>
                  <Button onClick={handleApplyPreparedJsonImport} disabled={jsonImportBusy || jsonImportDiff.total === 0}>
                    <FileJson size={16} /> {jsonImportBusy ? "Импортирую…" : "Применить изменения"}
                  </Button>
                </div>
              </div>
            </div>

            <div className="flex-1 overflow-y-auto px-4 py-4 sm:px-6">
              <div className="grid gap-3 sm:grid-cols-4">
                <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] p-3">
                  <div className="text-xs text-neutral-500">Всего</div>
                  <div className="mt-1 text-2xl font-semibold">{jsonImportDiff.total}</div>
                </div>
                <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] p-3">
                  <div className="text-xs text-neutral-500">Создать</div>
                  <div className="mt-1 text-2xl font-semibold">{jsonImportDiff.createCount}</div>
                </div>
                <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] p-3">
                  <div className="text-xs text-neutral-500">Обновить</div>
                  <div className="mt-1 text-2xl font-semibold">{jsonImportDiff.updateCount}</div>
                </div>
                <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] p-3">
                  <div className="text-xs text-neutral-500">Без изменений</div>
                  <div className="mt-1 text-2xl font-semibold">{jsonImportDiff.unchangedCount}</div>
                </div>
              </div>

              {(jsonImportDiff.withoutIdCount > 0 || jsonImportDiff.duplicateTitleCount > 0) && (
                <div className="mt-4 rounded-2xl border border-amber-300/70 bg-amber-50 px-4 py-3 text-sm leading-6 text-amber-900 dark:border-amber-700/60 dark:bg-amber-950/30 dark:text-amber-100">
                  <div className="flex items-start gap-2">
                    <AlertTriangle size={18} className="mt-0.5 shrink-0" />
                    <div>
                      {jsonImportDiff.withoutIdCount > 0 ? <div>Заданий без <code>id</code>: {jsonImportDiff.withoutIdCount}. Они будут созданы как новые, а не заменят существующие.</div> : null}
                      {jsonImportDiff.duplicateTitleCount > 0 ? <div>Есть новые задания с названием, которое уже встречается в курсе. Это может создать дубли, если нейронка не сохранила <code>id</code>.</div> : null}
                    </div>
                  </div>
                </div>
              )}

              <div className="mt-4 space-y-3 pb-2">
                {jsonImportDiff.rows.map((row) => (
                  <div key={`${row.index}-${row.id || row.title}`} className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--muted))]/20 p-3">
                    <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
                      <div className="min-w-0">
                        <div className="flex flex-wrap items-center gap-2">
                          <Badge variant={row.action === "create" ? "success" : row.action === "update" ? "outline" : "secondary"}>
                            {row.action === "create" ? "Создать" : row.action === "update" ? "Обновить" : "Без изменений"}
                          </Badge>
                          <Badge variant="outline">{row.type}</Badge>
                          {row.duplicateTitle ? <Badge intent="danger">возможный дубль</Badge> : null}
                        </div>
                        <div className="mt-2 break-words font-semibold">{row.title}</div>
                        <div className="mt-1 break-all text-xs text-neutral-500">id: {row.id || "нет id"}</div>
                      </div>
                      <div className="text-xs text-neutral-500">#{row.index + 1}</div>
                    </div>

                    {row.action === "create" ? (
                      <div className="mt-3 rounded-xl border border-dashed border-[rgba(var(--border)/0.75)] px-3 py-2 text-sm text-neutral-500">
                        Новое задание. Полный текст будет взят из JSON.
                      </div>
                    ) : row.changes.length ? (
                      <div className="mt-3 space-y-2">
                        {row.changes.map((change) => (
                          <div key={change.key} className="rounded-xl border border-[rgba(var(--border)/0.65)] p-3">
                            <div className="mb-2 text-sm font-semibold">{change.label}</div>
                            <div className="grid gap-2 lg:grid-cols-2">
                              <div>
                                <div className="mb-1 text-xs uppercase tracking-wide text-neutral-500">Было</div>
                                <pre className="max-h-44 overflow-auto whitespace-pre-wrap rounded-lg bg-[rgb(var(--card))] p-2 text-xs leading-5">{shortImportValue(change.before)}</pre>
                              </div>
                              <div>
                                <div className="mb-1 text-xs uppercase tracking-wide text-neutral-500">Станет</div>
                                <pre className="max-h-44 overflow-auto whitespace-pre-wrap rounded-lg bg-[rgb(var(--card))] p-2 text-xs leading-5">{shortImportValue(change.after)}</pre>
                              </div>
                            </div>
                          </div>
                        ))}
                      </div>
                    ) : (
                      <div className="mt-3 rounded-xl border border-dashed border-[rgba(var(--border)/0.75)] px-3 py-2 text-sm text-neutral-500">
                        Задание найдено по id, но в поддерживаемых полях изменений не обнаружено.
                      </div>
                    )}
                  </div>
                ))}
              </div>
            </div>
          </Card>
        </div>
      )}

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

                <button
                  type="button"
                  disabled={!!createBusyType || jsonImportBusy}
                  onClick={handleCreateChildCourse}
                  className="w-full rounded-2xl border border-[rgba(var(--border)/0.75)] bg-[rgb(var(--accent))]/10 p-4 text-left transition hover:-translate-y-0.5 hover:border-[rgb(var(--accent))]/70 hover:bg-[rgb(var(--accent))]/15 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  <div className="flex items-center justify-between gap-3">
                    <div className="font-semibold">Вложенный курс</div>
                    <Badge variant="outline">course</Badge>
                  </div>
                  <div className="mt-1 text-sm leading-5 text-neutral-500">Курс внутри текущего курса, в общем порядке вместе с заданиями.</div>
                  <div className="mt-2 text-xs text-neutral-400">{createBusyType === "course" ? "Создаю…" : "Можно сделать курс → курс → курс"}</div>
                </button>

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
                      Смешанный пример
                    </Button>
                  </div>
                </div>

                <div className="mt-4 rounded-2xl border border-dashed border-[rgba(var(--border)/0.85)] bg-[rgb(var(--card))]/70 p-3">
                  <div className="flex flex-col gap-1 sm:flex-row sm:items-end sm:justify-between">
                    <div>
                      <div className="text-sm font-semibold">Готовые примеры под каждый вид задания</div>
                      <div className="text-xs leading-5 text-neutral-500">
                        Кнопка «Копировать» сразу кладёт нужный шаблон в буфер обмена. Кнопка «В редактор» вставляет его в поле импорта ниже.
                      </div>
                    </div>
                  </div>
                  <div className="mt-3 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                    {JSON_IMPORT_EXAMPLES.map((example) => (
                      <div key={example.key} className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--muted))]/25 p-3">
                        <div className="flex items-start justify-between gap-2">
                          <div className="min-w-0">
                            <div className="truncate text-sm font-semibold">{example.title}</div>
                            <div className="mt-1 text-xs leading-5 text-neutral-500">{example.description}</div>
                          </div>
                          <Badge variant="outline" className="shrink-0">{example.type}</Badge>
                        </div>
                        <div className="mt-3 flex flex-wrap gap-2">
                          <Button variant="outline" onClick={() => handleCopyJsonExample(example)} disabled={jsonImportBusy || !!createBusyType}>
                            <Copy size={14} /> Копировать
                          </Button>
                          <Button variant="outline" onClick={() => handleUseJsonExample(example)} disabled={jsonImportBusy || !!createBusyType}>
                            В редактор
                          </Button>
                        </div>
                      </div>
                    ))}
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
                    Поддерживаемые поля: <code>id</code>, <code>title</code>, <code>description</code>, <code>type</code>, <code>language</code>, <code>allowedLanguages</code>, <code>starterCode</code>, <code>testCases</code>, <code>testSettings</code>, <code>questions</code>, <code>blocks</code>, <code>codeForbiddenCalls</code>, <code>codeRequiredCalls</code>, <code>imageTestReferenceKey</code>, <code>imageTestSimilarityThreshold</code>, <code>analyticsSettings</code>, <code>difficulty</code>, <code>rating</code>, <code>sort</code>, <code>tags</code>, <code>isVisible</code>.
                  </div>
                  <Button onClick={handlePrepareJsonImportDiff} disabled={jsonImportBusy || !!createBusyType}>
                    <GitCompare size={16} /> {jsonImportBusy ? "Готовлю дифф…" : "Показать дифф"}
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
        {filtered.map((entry, idx) => {
          const itemCanEdit = canReorderContentItem(entry);
          const itemPosition = positionByKey.get(entry.key) ?? idx + 1;
          const isDragged = draggedContentKey === entry.key;
          const isDropTarget = dragOverContentKey === entry.key;

          if (entry.kind === "course") {
            const child = entry.course;
            const title = child.title || `Курс ${itemPosition}`;
            const href = courseCanEdit && child.canEdit !== false ? `/courses/${child.id}/edit` : `/course/${child.id}`;
            const ViewWrap = ({ children }) => (
              <Link to={`/course/${child.id}`} className="block group">
                {children}
              </Link>
            );
            const CardMain = (
              <div className="assignment-card-main min-w-0">
                <div className="assignment-card-title-wrap">
                  <div className="assignment-card-title" title={title}>{title}</div>
                </div>
                {child.description ? (
                  <p className="mt-3 text-sm leading-6 text-neutral-500 line-clamp-3">{child.description}</p>
                ) : (
                  <p className="mt-3 text-sm leading-6 text-neutral-400 line-clamp-3">Описание пока не добавлено.</p>
                )}
                {(() => {
                  const progress = childProgressByCourseId[child.id] || { loading: true, total: 0, solved: 0, percent: 0 };
                  return (
                    <div className="mt-5 space-y-2">
                      <div className="text-xs font-medium text-neutral-500">
                        {progress.loading ? '—/—' : `${progress.solved ?? 0}/${progress.total ?? 0}`}
                      </div>
                      <div className="h-2 overflow-hidden rounded-full bg-neutral-200/70 dark:bg-white/10">
                        <div
                          className="h-full rounded-full bg-[rgb(var(--accent))] transition-all duration-500"
                          style={{ width: `${progress.total > 0 ? progress.percent : 0}%` }}
                        />
                      </div>
                    </div>
                  );
                })()}
              </div>
            );
            const baseCardClass =
              "assignment-card h-full transition hover:shadow-lg hover:-translate-y-0.5 border-[rgba(var(--accent)/0.35)] " +
              (isDragged ? "assignment-card--dragging " : "") +
              (isDropTarget ? "assignment-card--drop-target " : "") +
              (isDropTarget && dragOverContentMode === 'inside' ? "ring-2 ring-[rgb(var(--accent))] " : "") +
              (isDropTarget && dragOverContentMode === 'after' ? "border-b-4 border-b-[rgb(var(--accent))] " : "") +
              (isDropTarget && dragOverContentMode === 'before' ? "border-t-4 border-t-[rgb(var(--accent))] " : "");
            const CardBase = <Card className={baseCardClass}>{CardMain}</Card>;
            const EditorCard = (
              <Card
                role="link"
                tabIndex={0}
                draggable={itemCanEdit && sortMode === "default"}
                onClick={() => {
                  if (dragStartedRef.current) {
                    dragStartedRef.current = false;
                    return;
                  }
                  nav(href);
                }}
                onKeyDown={(e) => {
                  if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    nav(href);
                  }
                }}
                onDragStart={(e) => {
                  if (!itemCanEdit || sortMode !== "default") {
                    e.preventDefault();
                    return;
                  }
                  dragStartedRef.current = true;
                  e.dataTransfer.effectAllowed = "move";
                  e.dataTransfer.setData("text/plain", entry.key);
                  setDraggedContentKey(entry.key);
                }}
                onDragEnter={(e) => {
                  if (!draggedContentKey || draggedContentKey === entry.key) return;
                  e.preventDefault();
                  setDragOverContentKey(entry.key);
                  setDragOverContentMode(contentDropModeFromEvent(e, entry));
                }}
                onDragOver={(e) => {
                  if (!draggedContentKey || draggedContentKey === entry.key) return;
                  e.preventDefault();
                  e.dataTransfer.dropEffect = "move";
                  setDragOverContentMode(contentDropModeFromEvent(e, entry));
                }}
                onDragLeave={() => {
                  if (dragOverContentKey === entry.key) {
                    setDragOverContentKey(null);
                    setDragOverContentMode('before');
                  }
                }}
                onDrop={(e) => {
                  e.preventDefault();
                  const sourceKey = e.dataTransfer.getData("text/plain");
                  handleDropOnContentItem(entry.key, sourceKey, contentDropModeFromEvent(e, entry));
                }}
                onDragEnd={() => {
                  setDraggedContentKey(null);
                  setDragOverContentKey(null);
                  setDragOverContentMode('before');
                  setExtractDropActive(false);
                  setTimeout(() => {
                    dragStartedRef.current = false;
                  }, 0);
                }}
                className={baseCardClass + (itemCanEdit && sortMode === "default" ? " cursor-move" : " cursor-pointer")}
                title={sortMode === "default" ? "Перетащи карточку, чтобы изменить общий порядок" : "Открыть курс"}
              >
                {CardMain}
              </Card>
            );
            return (
              <IfEditor key={entry.key} otherwise={<ViewWrap>{CardBase}</ViewWrap>}>
                {itemCanEdit ? EditorCard : <ViewWrap>{CardBase}</ViewWrap>}
              </IfEditor>
            );
          }

          const a = entry.assignment;
          const solved = isAssignmentSolved(a);
          const title = previewAssignmentTitle(a.title, `Задание ${itemPosition}`);
          const assignmentCanEdit = itemCanEdit;
          const hasAssignmentMeta = Boolean(
            a.isAiDraft ||
            a.isHidden ||
            (a.lifecycleStatus && a.lifecycleStatus !== 'published')
          );

          const ViewWrap = ({ children }) => (
            <Link to={`/assignment/${a.id}`} className="block group">
              {children}
            </Link>
          );
          const CardMain = (
            <div className="assignment-card-main min-w-0">
              {hasAssignmentMeta && (
                <div className="assignment-card-heading">
                  <div className="flex flex-wrap items-center gap-1.5">
                    {a.isAiDraft && <Badge variant="secondary">AI-черновик</Badge>}
                    {a.isHidden && <Badge variant="outline">скрыто</Badge>}
                    {a.lifecycleStatus && a.lifecycleStatus !== 'published' && <Badge variant="outline">{a.lifecycleStatus}</Badge>}
                  </div>
                </div>
              )}

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
            (isDragged ? "assignment-card--dragging " : "") +
            (isDropTarget ? "assignment-card--drop-target " : "") +
            (isDropTarget && dragOverContentMode === 'after' ? "border-b-4 border-b-[rgb(var(--accent))] " : "") +
            (isDropTarget && dragOverContentMode === 'before' ? "border-t-4 border-t-[rgb(var(--accent))] " : "");

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
                e.dataTransfer.setData("text/plain", entry.key);
                setDraggedContentKey(entry.key);
              }}
              onDragEnter={(e) => {
                if (!draggedContentKey || draggedContentKey === entry.key) return;
                e.preventDefault();
                setDragOverContentKey(entry.key);
                setDragOverContentMode(contentDropModeFromEvent(e, entry));
              }}
              onDragOver={(e) => {
                if (!draggedContentKey || draggedContentKey === entry.key) return;
                e.preventDefault();
                e.dataTransfer.dropEffect = "move";
                setDragOverContentMode(contentDropModeFromEvent(e, entry));
              }}
              onDragLeave={() => {
                if (dragOverContentKey === entry.key) {
                  setDragOverContentKey(null);
                  setDragOverContentMode('before');
                }
              }}
              onDrop={(e) => {
                e.preventDefault();
                const sourceKey = e.dataTransfer.getData("text/plain");
                handleDropOnContentItem(entry.key, sourceKey, contentDropModeFromEvent(e, entry));
              }}
              onDragEnd={() => {
                setDraggedContentKey(null);
                setDragOverContentKey(null);
                setDragOverContentMode('before');
                setExtractDropActive(false);
                setTimeout(() => {
                  dragStartedRef.current = false;
                }, 0);
              }}
              className={baseCardClass + (assignmentCanEdit && sortMode === "default" ? " cursor-move" : " cursor-pointer")}
              title={sortMode === "default" ? "Перетащи карточку, чтобы изменить общий порядок" : "Открыть редактор задания"}
            >
              {CardMain}
            </Card>
          );

          return (
            <IfEditor key={entry.key} otherwise={<ViewWrap>{CardBase}</ViewWrap>}>
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
