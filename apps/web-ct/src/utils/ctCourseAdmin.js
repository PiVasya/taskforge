import {
  EXAM_CODE,
  SUBJECT_CODE,
  getSectionSortOrder,
  normalizeSectionCode,
} from "../data/ctSections";
import { createLearningCourse } from "../api/learning";

export const CT_ROOT_SLUG = `${SUBJECT_CODE}-${EXAM_CODE}`;
export const CT_ROOT_TITLE = "Русский язык — ЦТ/ЦЭ";

export function flattenCourses(nodes, level = 0, result = []) {
  (nodes || []).forEach((node) => {
    result.push({ ...node, level });
    flattenCourses(node.children, level + 1, result);
  });
  return result;
}

export function findCtRootCourse(courses) {
  return (
    (courses || []).find((course) => course.slug === CT_ROOT_SLUG) ||
    (courses || []).find(
      (course) =>
        course.subjectCode === SUBJECT_CODE &&
        course.examCode === EXAM_CODE &&
        !course.sectionCode,
    ) ||
    null
  );
}

export function findCourseBySection(courses, sectionCode) {
  const normalized = normalizeSectionCode(sectionCode);
  return (
    (courses || []).find(
      (course) => normalizeSectionCode(course.sectionCode) === normalized,
    ) || null
  );
}

export function makeCtRootPayload() {
  return {
    parentCourseId: null,
    slug: CT_ROOT_SLUG,
    title: CT_ROOT_TITLE,
    shortTitle: "ЦТ/ЦЭ",
    summary: "Подготовка к ЦТ/ЦЭ.",
    description: "",
    subjectCode: SUBJECT_CODE,
    examCode: EXAM_CODE,
    sectionCode: null,
    sortOrder: 10,
    isPublished: true,
  };
}

export function makeSectionCoursePayload(sectionCode, parentCourseId) {
  const normalized = normalizeSectionCode(sectionCode);
  return {
    parentCourseId,
    slug: normalized.toLowerCase(),
    title: normalized,
    shortTitle: normalized,
    summary: `Материалы и задания ${normalized}.`,
    description: "",
    subjectCode: SUBJECT_CODE,
    examCode: EXAM_CODE,
    sectionCode: normalized,
    sortOrder: getSectionSortOrder(normalized),
    isPublished: true,
  };
}

export async function ensureCtRootCourse(courses) {
  const root = findCtRootCourse(courses);
  if (root) return root;
  return createLearningCourse(makeCtRootPayload());
}

export async function createCtSectionCourse(sectionCode, courses = []) {
  const normalized = normalizeSectionCode(sectionCode);
  if (!normalized)
    throw new Error("Номер должен быть в формате A1, A31, B1, B11 и т.п.");

  const existing = findCourseBySection(courses, normalized);
  if (existing) return existing;

  const root = await ensureCtRootCourse(courses);
  return createLearningCourse(makeSectionCoursePayload(normalized, root.id));
}
