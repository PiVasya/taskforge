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


function progressSnapshotEqual(left, right) {
  if (left === right) return true;
  if (!left || !right) return false;
  return Number(left.total || 0) === Number(right.total || 0)
    && Number(left.solved || 0) === Number(right.solved || 0)
    && Number(left.percent || 0) === Number(right.percent || 0)
    && Boolean(left.isComplete) === Boolean(right.isComplete)
    && Boolean(left.loading) === Boolean(right.loading)
    && Boolean(left.failed) === Boolean(right.failed);
}

function reuseProgressMapIfEqual(previous, next) {
  if (previous === next) return previous;
  const previousKeys = Object.keys(previous || {}).sort();
  const nextKeys = Object.keys(next || {}).sort();
  if (previousKeys.length !== nextKeys.length) return next;
  for (let index = 0; index < previousKeys.length; index += 1) {
    const key = previousKeys[index];
    if (key !== nextKeys[index] || !progressSnapshotEqual(previous?.[key], next?.[key])) return next;
  }
  return previous;
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
  if (item?.kind === "course") return String(item.course?.title || "");
  if (item?.kind === "locked") return String(item.locked?.title || "Продолжение закрыто");
  return previewAssignmentTitle(item?.assignment?.title, "");
}

function contentCreatedAt(item) {
  if (item?.kind === "course") return item.course?.createdAt;
  if (item?.kind === "locked") return null;
  return item?.assignment?.createdAt;
}

function compareContentItems(a, b) {
  const bySort = contentSortValue(a) - contentSortValue(b);
  if (bySort !== 0) return bySort;
  if (a.kind !== b.kind) {
    const rank = { course: 0, assignment: 1, locked: 2 };
    return (rank[a.kind] ?? 9) - (rank[b.kind] ?? 9);
  }
  return contentTitle(a).localeCompare(contentTitle(b), "ru", { sensitivity: "base" });
}


function collectLearnerVisibleEntityIds(learningMap) {
  const courseIds = new Set();
  const assignmentIds = new Set();
  const nodes = Array.isArray(learningMap?.document?.nodes) ? learningMap.document.nodes : [];

  for (const node of nodes) {
    const entityId = String(node?.entityId || '').trim();
    if (!entityId || String(node?.type || '').toLowerCase() === 'locked') continue;
    if (String(node?.type || '').toLowerCase() === 'course') courseIds.add(entityId);
    else assignmentIds.add(entityId);
  }

  return { courseIds, assignmentIds };
}


function filterLearnerCardCourses(courses, parentCourseId, visibleCourseIds) {
  const visible = visibleCourseIds instanceof Set
    ? visibleCourseIds
    : new Set((Array.isArray(visibleCourseIds) ? visibleCourseIds : []).map((id) => String(id)));
  const parentId = String(parentCourseId || '');
  return (Array.isArray(courses) ? courses : []).filter((course) => (
    String(course?.parentCourseId || '') === parentId
    && visible.has(String(course?.id || ''))
  ));
}

function filterLearnerCardAssignments(assignments, visibleAssignmentIds) {
  const visible = visibleAssignmentIds instanceof Set
    ? visibleAssignmentIds
    : new Set((Array.isArray(visibleAssignmentIds) ? visibleAssignmentIds : []).map((id) => String(id)));
  return (Array.isArray(assignments) ? assignments : []).filter((assignment) => (
    visible.has(String(assignment?.id || ''))
  ));
}

function collectLearnerCardLocks(learningMap, courseId) {
  const requestedCourseId = String(courseId || '').trim();
  if (!requestedCourseId) return [];

  const nodes = Array.isArray(learningMap?.document?.nodes) ? learningMap.document.nodes.filter(Boolean) : [];
  const edges = Array.isArray(learningMap?.document?.edges) ? learningMap.document.edges.filter(Boolean) : [];
  const byId = new Map(nodes.map((node) => [String(node?.id || ''), node]).filter(([id]) => id));
  const incoming = new Map();

  for (const edge of edges) {
    const source = String(edge?.source || '').trim();
    const target = String(edge?.target || '').trim();
    if (!source || !target || !byId.has(source) || !byId.has(target)) continue;
    if (!incoming.has(target)) incoming.set(target, []);
    incoming.get(target).push(source);
  }

  const nearestCourseIds = (startId) => {
    const found = new Set();
    const seen = new Set();
    const queue = [...(incoming.get(startId) || [])];

    while (queue.length) {
      const nodeId = queue.shift();
      if (!nodeId || seen.has(nodeId)) continue;
      seen.add(nodeId);
      const node = byId.get(nodeId);
      if (!node) continue;
      if (String(node?.type || '').toLowerCase() === 'course') {
        const entityId = String(node?.entityId || '').trim();
        if (entityId) found.add(entityId);
        continue;
      }
      queue.push(...(incoming.get(nodeId) || []));
    }
    return found;
  };

  return nodes
    .filter((node) => String(node?.type || '').toLowerCase() === 'locked')
    .filter((node) => nearestCourseIds(String(node?.id || '')).has(requestedCourseId))
    .map((node, index) => {
      const sourceEntityIds = (incoming.get(String(node?.id || '')) || [])
        .map((sourceId) => byId.get(sourceId))
        .filter(Boolean)
        .filter((source) => String(source?.type || '').toLowerCase() !== 'locked')
        .map((source) => String(source?.entityId || '').trim())
        .filter(Boolean);
      const settings = node?.settings || node?.data?.settings || {};
      return {
        id: String(node?.id || `locked-${index}`),
        title: String(settings?.title || 'Продолжение закрыто'),
        requirement: String(settings?.requirement || 'Решите предыдущее задание, чтобы открыть продолжение.'),
        sourceEntityIds: [...new Set(sourceEntityIds)],
        x: Number(node?.position?.x) || 0,
        y: Number(node?.position?.y) || 0,
      };
    })
    .sort((a, b) => (a.x - b.x) || (a.y - b.y) || a.id.localeCompare(b.id, 'ru'));
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

function makeLockedContentItem(lock, sort = Number.MAX_SAFE_INTEGER) {
  return {
    kind: 'locked',
    key: contentKey('locked', lock?.id || `lock-${sort}`),
    id: lock?.id || '',
    sort,
    title: lock?.title || 'Продолжение закрыто',
    description: lock?.requirement || 'Решите предыдущее задание, чтобы открыть продолжение.',
    locked: {
      id: lock?.id || '',
      title: lock?.title || 'Продолжение закрыто',
      requirement: lock?.requirement || 'Решите предыдущее задание, чтобы открыть продолжение.',
      sourceEntityIds: Array.isArray(lock?.sourceEntityIds) ? lock.sourceEntityIds : [],
    },
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
  progressSnapshotEqual,
  reuseProgressMapIfEqual,
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
  collectLearnerVisibleEntityIds,
  filterLearnerCardCourses,
  filterLearnerCardAssignments,
  collectLearnerCardLocks,
  makeCourseContentItem,
  makeAssignmentContentItem,
  makeLockedContentItem,
  CREATE_OPTIONS,
  buildDefaultAssignmentPayload,
};
