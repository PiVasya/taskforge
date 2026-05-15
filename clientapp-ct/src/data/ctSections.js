export const SUBJECT_CODE = "russian";
export const EXAM_CODE = "ct-ce-2026";
export const RANDOM_TASKS_COUNT = 5;

export const CT_PARTS = [
  {
    code: "A",
    title: "Часть A",
    description:
      "Задания с выбором ответа. Для ученика: открыл номер, прочитал HTML-конспект, решил случайные задания.",
    count: 30,
  },
  {
    code: "B",
    title: "Часть B",
    description:
      "Задания с кратким ответом. Такая же страница: конспект сверху, практика снизу.",
    count: 10,
  },
];

export const CT_SECTIONS = CT_PARTS.flatMap((part) =>
  Array.from({ length: part.count }, (_, index) => ({
    code: `${part.code}${index + 1}`,
    partCode: part.code,
    number: index + 1,
    isDefault: true,
  })),
);

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
    isDefault: isKnownSectionCode(code),
  };
}

export function getSectionPath(sectionCode) {
  return `/${normalizeSectionCode(sectionCode).toLowerCase()}`;
}

export function isKnownSectionCode(sectionCode) {
  const normalized = normalizeSectionCode(sectionCode);
  return CT_SECTIONS.some((section) => section.code === normalized);
}

export function getPartConfig(partCode) {
  return (
    CT_PARTS.find(
      (part) => part.code === String(partCode || "").toUpperCase(),
    ) || null
  );
}

export function getSectionsByPart(partCode) {
  return CT_SECTIONS.filter((section) => section.partCode === partCode);
}

export function getSectionSortOrder(sectionCode) {
  const section = parseSectionCode(sectionCode);
  if (!section) return 999999;
  return section.partCode === "A" ? section.number : 1000 + section.number;
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

export function mergeSectionsWithCourses(defaultSections, courses, partCode) {
  const byCode = new Map();

  (defaultSections || []).forEach((section) => {
    const parsed = parseSectionCode(section.code);
    if (parsed?.partCode === partCode)
      byCode.set(parsed.code, { ...parsed, isDefault: true });
  });

  (courses || []).forEach((course) => {
    const parsed = parseSectionCode(course.sectionCode);
    if (parsed?.partCode !== partCode) return;
    byCode.set(parsed.code, {
      ...parsed,
      isDefault: byCode.get(parsed.code)?.isDefault || false,
      courseId: course.id,
      course,
    });
  });

  return [...byCode.values()].sort((a, b) => a.number - b.number);
}
