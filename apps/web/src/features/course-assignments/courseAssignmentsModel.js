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
  { type: "sql-test", title: "SQL / Database" },
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

  if (normalized === "sql-test") return { ...base, title: "SQL", tags: "SQL", isHidden: true, isVisible: false };

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
      testSettings: { maxAttempts: 1, unlimitedAttempts: false, passPercent: 60, shuffleQuestions: true, shuffleAnswers: true, allowReview: true, attemptTimeLimitsSeconds: [] },
      questions: [],
    };
  }

  if (normalized === "math") {
    return {
      ...base,
      testSettings: { maxAttempts: 1, unlimitedAttempts: false, passPercent: 60, shuffleBlocks: false, allowReview: true, attemptTimeLimitsSeconds: [] },
      blocks: [],
    };
  }

  return base;
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
  buildDefaultAssignmentPayload,
};
