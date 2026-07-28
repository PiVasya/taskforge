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
  { type: "code-test", title: "Code-test" },
  { type: "test", title: "Test" },
  { type: "image-test", title: "Image-test" },
  { type: "math", title: "Math" },
];

const ZERO_GUID = "00000000-0000-0000-0000-000000000000";

function makeImportExamplePayload(assignments) {
  return {
    schemaVersion: 2,
    format: "taskforge-course-assignment-import",
    assignments,
  };
}

const CODE_TEST_ASSIGNMENT_EXAMPLE = makeImportExamplePayload([
  {
    type: "code-test",
    title: "Сумма двух чисел",
    description: "Считайте два целых числа и выведите их сумму.",
    language: "cpp",
    allowedLanguages: ["cpp", "python"],
    starterCode: "#include <iostream>\nusing namespace std;\n\nint main()\n{\n    // Ваш код здесь\n\n    return 0;\n}\n",
    testCases: [
      { input: "2 4", expectedOutput: "6", isHidden: false },
      { input: "-5 12", expectedOutput: "7", isHidden: false },
      { input: "100 250", expectedOutput: "350", isHidden: true },
    ],
    codeRequiredCalls: ["cin", "cout"],
    codeForbiddenCalls: ["system", "exec", "fork"],
    difficulty: 1,
    rating: 1,
    sort: 0,
    tags: "ОАИП, код",
    isVisible: true,
  },
]);

const TEST_ASSIGNMENT_EXAMPLE = makeImportExamplePayload([
  {
    type: "test",
    title: "Мини-тест",
    description: "Ответьте на вопросы.",
    testSettings: {
      maxAttempts: 2,
      passPercent: 70,
      shuffleQuestions: true,
      shuffleAnswers: true,
      allowReview: true,
      attemptTimeLimitsSeconds: [],
    },
    questions: [
      {
        id: ZERO_GUID,
        order: 0,
        type: "single-choice",
        prompt: "Какой тип JSON хранит true/false?",
        options: [
          { key: "a", text: "string" },
          { key: "b", text: "boolean" },
          { key: "c", text: "array" },
        ],
        correctOptionKeys: ["b"],
        acceptedAnswers: [],
        caseSensitive: false,
        trim: true,
      },
      {
        id: ZERO_GUID,
        order: 1,
        type: "text",
        prompt: "Расширение JSON без точки.",
        options: [],
        correctOptionKeys: [],
        acceptedAnswers: ["json"],
        caseSensitive: false,
        trim: true,
      },
    ],
    difficulty: 1,
    rating: 1,
    sort: 0,
    tags: "теория, тест",
    isVisible: true,
  },
]);

const MATH_ASSIGNMENT_EXAMPLE = makeImportExamplePayload([
  {
    type: "math",
    title: "Линейное уравнение",
    description: "Найдите x.",
    testSettings: {
      maxAttempts: 2,
      passPercent: 75,
      shuffleBlocks: false,
      allowReview: true,
      attemptTimeLimitsSeconds: [],
    },
    blocks: [
      {
        id: ZERO_GUID,
        order: 0,
        kind: "info",
        prompt: "2x + 6 = 14",
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
        prompt: "x =",
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
    ],
    difficulty: 1,
    rating: 1,
    sort: 0,
    tags: "математика",
    isVisible: true,
  },
]);

const IMAGE_TEST_ASSIGNMENT_EXAMPLE = makeImportExamplePayload([
  {
    type: "image-test",
    title: "Диагональ",
    description: "Нарисуйте диагональ на изображении 200x200.",
    language: "python",
    allowedLanguages: ["python"],
    starterCode: "import turtle\n\nt = turtle.Turtle()\nt.goto(100, -100)\nturtle.done()\n",
    imageTestSimilarityThreshold: 90,
    testCases: [
      {
        input: "",
        expectedOutput: "",
        isHidden: false,
        expectedImageBase64: "",
        expectedImageContentType: "image/png",
        expectedImageFileName: "reference.png",
      },
    ],
    difficulty: 1,
    rating: 1,
    sort: 0,
    tags: "графика",
    isVisible: true,
  },
]);

const MIXED_ASSIGNMENT_EXAMPLE = makeImportExamplePayload([
  CODE_TEST_ASSIGNMENT_EXAMPLE.assignments[0],
  TEST_ASSIGNMENT_EXAMPLE.assignments[0],
  MATH_ASSIGNMENT_EXAMPLE.assignments[0],
  IMAGE_TEST_ASSIGNMENT_EXAMPLE.assignments[0],
]);

const JSON_IMPORT_EXAMPLES = [
  { key: "code-test", title: "Code-test", type: "code-test", payload: CODE_TEST_ASSIGNMENT_EXAMPLE },
  { key: "test", title: "Test", type: "test", payload: TEST_ASSIGNMENT_EXAMPLE },
  { key: "math", title: "Math", type: "math", payload: MATH_ASSIGNMENT_EXAMPLE },
  { key: "image-test", title: "Image-test", type: "image-test", payload: IMAGE_TEST_ASSIGNMENT_EXAMPLE },
  { key: "mixed", title: "Смешанный", type: "mixed", payload: MIXED_ASSIGNMENT_EXAMPLE },
];

const JSON_IMPORT_DOC_FIELDS = [
  "id",
  "type",
  "title",
  "description",
  "language",
  "allowedLanguages",
  "starterCode",
  "testCases",
  "testSettings",
  "questions",
  "blocks",
  "codeForbiddenCalls",
  "codeRequiredCalls",
  "imageTestReferenceKey",
  "imageTestSimilarityThreshold",
  "analyticsSettings",
  "difficulty",
  "rating",
  "sort",
  "tags",
  "isVisible",
];


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

function normalizeImportTypeValue(value) {
  const s = String(value || "").trim().toLowerCase();
  if (["code", "code_test", "code-test", "programming"].includes(s)) return "code-test";
  if (["image", "image_test", "image-test", "graphics"].includes(s)) return "image-test";
  if (["quiz", "test", "questions"].includes(s)) return "test";
  if (["math", "math-test", "math_test"].includes(s)) return "math";
  return s;
}

function inferImportTypeValue(item) {
  if (!item || typeof item !== "object") return "code-test";
  const explicit = normalizeImportTypeValue(item.type || item.kind || item.assignmentType);
  if (explicit) return explicit;
  if (Array.isArray(item.questions) || item.tests?.questions || item.testSpec?.questions) return "test";
  if (Array.isArray(item.blocks) || item.tests?.blocks || item.mathSpec?.blocks) return "math";
  if (item.imageTestReferenceKey !== undefined || item.imageTestSimilarityThreshold !== undefined || item.expectedImageKey !== undefined) return "image-test";
  return "code-test";
}

function hasImportArrayPayload(item, names) {
  if (!item || typeof item !== "object") return false;
  for (const name of names) {
    const value = item[name];
    if (Array.isArray(value) && value.length > 0) return true;
  }
  if (item.tests && typeof item.tests === "object") {
    for (const name of names) {
      const value = item.tests[name];
      if (Array.isArray(value) && value.length > 0) return true;
    }
  }
  if (item.testSpec && typeof item.testSpec === "object") {
    for (const name of names) {
      const value = item.testSpec[name];
      if (Array.isArray(value) && value.length > 0) return true;
    }
  }
  if (item.mathSpec && typeof item.mathSpec === "object") {
    for (const name of names) {
      const value = item.mathSpec[name];
      if (Array.isArray(value) && value.length > 0) return true;
    }
  }
  return false;
}

function validateJsonImportItem(item, index, action) {
  const issues = [];
  const isCreate = action === "create";
  if (!item || typeof item !== "object" || Array.isArray(item)) {
    return [`#${index + 1}: ожидался объект задания.`];
  }

  const type = inferImportTypeValue(item);
  const explicitType = item.type || item.kind || item.assignmentType;
  const title = String(item.title || item.name || item.assignmentTitle || "").trim();
  const supportedTypes = ["code-test", "image-test", "test", "math"];

  if (explicitType && !supportedTypes.includes(type)) issues.push(`type должен быть code-test, image-test, test или math.`);
  if (isCreate && !title) issues.push("title обязателен для нового задания.");
  if (title.length > 200) issues.push("title не должен быть длиннее 200 символов.");

  if (item.difficulty !== undefined) {
    const difficulty = Number(item.difficulty);
    if (!Number.isInteger(difficulty) || difficulty < 1 || difficulty > 3) issues.push("difficulty должен быть 1, 2 или 3.");
  }
  if (item.rating !== undefined && Number(item.rating) < 0) issues.push("rating не может быть отрицательным.");
  if (item.sort !== undefined && Number(item.sort) < 0) issues.push("sort не может быть отрицательным.");

  if (isCreate && ["code-test", "image-test"].includes(type) && !hasImportArrayPayload(item, ["testCases", "cases", "tests"])) {
    issues.push("для code-test/image-test нужно указать непустой testCases.");
  }
  if (isCreate && type === "test" && !hasImportArrayPayload(item, ["questions"])) {
    issues.push("для test нужно указать непустой questions.");
  }
  if (isCreate && type === "math" && !hasImportArrayPayload(item, ["blocks"])) {
    issues.push("для math нужно указать непустой blocks.");
  }

  return issues;
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

    const action = existing ? (changes.length ? "update" : "unchanged") : "create";
    const type = item?.type || existing?.type || inferImportTypeValue(item);
    const issues = validateJsonImportItem(item, index, action);

    return {
      index,
      id,
      title,
      type,
      action,
      duplicateTitle,
      changes,
      issues,
    };
  });

  return {
    total: rows.length,
    createCount: rows.filter((x) => x.action === "create").length,
    updateCount: rows.filter((x) => x.action === "update").length,
    unchangedCount: rows.filter((x) => x.action === "unchanged").length,
    withoutIdCount: rows.filter((x) => !x.id).length,
    duplicateTitleCount: rows.filter((x) => x.duplicateTitle).length,
    validationErrorCount: rows.reduce((sum, row) => sum + row.issues.length, 0),
    rows,
  };
}


export {
  previewAssignmentTitle,
  previewAssignmentDescription,
  isAssignmentSolved,
  normalizeProgressRows,
  buildChildrenByParent,
  collectCourseSubtreeIds,
  sumCourseProgress,
  SORT_OPTIONS,
  compareCourses,
  contentKey,
  contentTitle,
  contentCreatedAt,
  compareContentItems,
  makeCourseContentItem,
  makeAssignmentContentItem,
  CREATE_OPTIONS,
  JSON_IMPORT_EXAMPLES,
  JSON_IMPORT_DOC_FIELDS,
  buildDefaultAssignmentPayload,
  summarizeImportPayload,
  shortImportValue,
  buildJsonImportDiff,
};
