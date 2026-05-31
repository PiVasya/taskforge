export const SUBJECT_CODE = "russian";
export const EXAM_CODE = "ct-ce-2026";
export const RANDOM_TASKS_COUNT = 5;

export const CT_PARTS = [
  {
    code: "A",
    title: "Часть A",
    description:
      "Задания с выбором ответа. В списке отображаются только реально созданные номера.",
  },
  {
    code: "B",
    title: "Часть B",
    description:
      "Задания с кратким ответом. Новые номера добавляются вручную через плюс в этой части.",
  },
];

export const CT_SECTIONS = [
  ...Array.from({ length: 30 }, (_, index) => ({
    code: `A${index + 1}`,
    partCode: "A",
    number: index + 1,
    isTemplate: true,
  })),
  ...Array.from({ length: 10 }, (_, index) => ({
    code: `B${index + 1}`,
    partCode: "B",
    number: index + 1,
    isTemplate: true,
  })),
];

export function normalizeSectionCode(raw) {
  const value = String(raw || "")
    .trim()
    .toUpperCase()
    .replace(/[^A-ZА-Я0-9]/g, "")
    .replace(/^А/, "A")
    .replace(/^В/, "B");

  const match = value.match(/^([AB])(\d+)$/);
  if (!match) return "";
  const number = Number(match[2]);
  if (!Number.isFinite(number) || number <= 0) return "";
  return `${match[1]}${number}`;
}

export function parseSectionCode(raw) {
  const code = normalizeSectionCode(raw);
  const match = code.match(/^([AB])(\d+)$/);
  if (!match) return null;
  return {
    code,
    partCode: match[1],
    number: Number(match[2]),
  };
}

export function getSectionPath(sectionCode) {
  return `/${normalizeSectionCode(sectionCode).toLowerCase()}`;
}

export function isKnownSectionCode(sectionCode) {
  return Boolean(parseSectionCode(sectionCode));
}

export function getPartConfig(partCode) {
  return (
    CT_PARTS.find(
      (part) => part.code === String(partCode || "").toUpperCase(),
    ) || null
  );
}

export function getSectionsByPart(partCode) {
  const part = String(partCode || "").toUpperCase();
  return CT_SECTIONS.filter((section) => section.partCode === part);
}

export function getSectionSortOrder(sectionCode) {
  const section = parseSectionCode(sectionCode);
  if (!section) return 999999;
  return section.partCode === "A" ? section.number : 1000 + section.number;
}

export function sectionFromCourse(course) {
  const parsed = parseSectionCode(course?.sectionCode);
  if (!parsed) return null;
  return {
    ...parsed,
    courseId: course.id,
    isPublished: course.isPublished !== false,
    course,
  };
}

export function getCreatedSectionsByPart(courses, partCode) {
  const part = String(partCode || "").toUpperCase();
  const byCode = new Map();

  (courses || []).forEach((course) => {
    const section = sectionFromCourse(course);
    if (!section || section.partCode !== part) return;
    byCode.set(section.code, section);
  });

  return [...byCode.values()].sort((a, b) => a.number - b.number);
}

export function findSectionCourse(courses, sectionCode) {
  const normalized = normalizeSectionCode(sectionCode);
  if (!normalized) return null;
  return (
    (courses || []).find(
      (course) => normalizeSectionCode(course.sectionCode) === normalized,
    ) || null
  );
}

export function getNextSectionCode(partCode, sections = []) {
  const part = String(partCode || "").toUpperCase();
  const max = sections
    .map((section) =>
      parseSectionCode(section.code || section.sectionCode || section),
    )
    .filter((section) => section?.partCode === part)
    .reduce((value, section) => Math.max(value, section.number), 0);
  return `${part}${max + 1}`;
}

export function mergeSectionsWithCourses(_defaultSections, courses, partCode) {
  return getCreatedSectionsByPart(courses, partCode);
}
