export const SUBJECT_CODE = 'russian';
export const EXAM_CODE = 'ct-ce-2026';
export const RANDOM_TASKS_COUNT = 5;

// Базовая сетка под классический формат ЦТ по русскому языку: A1-A30 и B1-B10.
// Если под конкретный год/предмет нужна другая сетка, меняется только count ниже.
export const CT_PARTS = [
  {
    code: 'A',
    title: 'Часть A',
    description: 'Задания с выбором ответа. Для ученика: открыл номер, прочитал HTML-конспект, решил случайные задания.',
    count: 30,
  },
  {
    code: 'B',
    title: 'Часть B',
    description: 'Задания с кратким ответом. Такая же страница: конспект сверху, практика снизу.',
    count: 10,
  },
];

export const CT_SECTIONS = CT_PARTS.flatMap((part) => (
  Array.from({ length: part.count }, (_, index) => ({
    code: `${part.code}${index + 1}`,
    partCode: part.code,
    number: index + 1,
  }))
));

export function normalizeSectionCode(raw) {
  const value = String(raw || '')
    .trim()
    .toUpperCase()
    .replace(/[^A-ZА-Я0-9]/g, '')
    .replace(/^А/, 'A')
    .replace(/^В/, 'B');

  const match = value.match(/^([A-Z])(\d{1,2})$/);
  if (!match) return '';
  return `${match[1]}${Number(match[2])}`;
}

export function getSectionPath(sectionCode) {
  return `/${normalizeSectionCode(sectionCode).toLowerCase()}`;
}

export function isKnownSectionCode(sectionCode) {
  const normalized = normalizeSectionCode(sectionCode);
  return CT_SECTIONS.some((section) => section.code === normalized);
}

export function getSectionsByPart(partCode) {
  return CT_SECTIONS.filter((section) => section.partCode === partCode);
}
