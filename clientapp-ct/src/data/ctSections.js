export const SUBJECT_CODE = 'russian';
export const EXAM_CODE = 'ct-ce-2026';
export const RANDOM_TASKS_COUNT = 5;

export const CT_PARTS = [
  {
    code: 'A',
    title: 'Часть A',
    description: 'Задания с выбором ответа. Стартовая сетка A1-A30 не ограничивает редактора: можно вручную добавить A31, A32 и дальше.',
    count: 30,
  },
  {
    code: 'B',
    title: 'Часть B',
    description: 'Задания с кратким ответом. Стартовая сетка B1-B10 не ограничивает редактора: можно вручную добавить B11, B12 и дальше.',
    count: 10,
  },
];

export const CT_SECTIONS = CT_PARTS.flatMap((part) => (
  Array.from({ length: part.count }, (_, index) => ({
    code: `${part.code}${index + 1}`,
    partCode: part.code,
    number: index + 1,
    isDefault: true,
  }))
));

export function normalizeSectionCode(raw) {
  const value = String(raw || '')
    .trim()
    .toUpperCase()
    .replace(/[^A-ZА-Я0-9]/g, '')
    .replace(/^А/, 'A')
    .replace(/^В/, 'B');

  const match = value.match(/^([AB])(\d+)$/);
  if (!match) return '';
  const number = Number(match[2]);
  if (!Number.isInteger(number) || number < 1) return '';
  return `${match[1]}${number}`;
}

export function sectionPartCode(sectionCode) {
  return normalizeSectionCode(sectionCode).match(/^([AB])/)?.[1] || '';
}

export function sectionNumber(sectionCode) {
  const match = normalizeSectionCode(sectionCode).match(/^(?:A|B)(\d+)$/);
  return match ? Number(match[1]) : 0;
}

export function sectionSortOrder(sectionCode) {
  const normalized = normalizeSectionCode(sectionCode);
  const part = sectionPartCode(normalized);
  const number = sectionNumber(normalized);
  if (part === 'A') return number;
  if (part === 'B') return 1000 + number;
  return 999999;
}

export function makeSection(sectionCode, extra = {}) {
  const normalized = normalizeSectionCode(sectionCode);
  return {
    code: normalized,
    partCode: sectionPartCode(normalized),
    number: sectionNumber(normalized),
    isDefault: false,
    ...extra,
  };
}

export function getSectionPath(sectionCode) {
  const normalized = normalizeSectionCode(sectionCode);
  return normalized ? `/${normalized.toLowerCase()}` : '/';
}

export function isKnownSectionCode(sectionCode) {
  return Boolean(normalizeSectionCode(sectionCode));
}

export function getSectionsByPart(partCode, extraSections = []) {
  const map = new Map();
  CT_SECTIONS.filter((section) => section.partCode === partCode).forEach((section) => map.set(section.code, section));
  (extraSections || [])
    .map((section) => (typeof section === 'string' ? makeSection(section) : makeSection(section.code || section.sectionCode, section)))
    .filter((section) => section.code && section.partCode === partCode)
    .forEach((section) => map.set(section.code, section));
  return [...map.values()].sort((a, b) => a.number - b.number);
}
