export function normalizedCourseDeleteTitle(value) {
  return String(value ?? '').trim();
}

export function canConfirmCourseDeletion({ expectedTitle, typedTitle, acknowledged, busy = false }) {
  const expected = normalizedCourseDeleteTitle(expectedTitle);
  const typed = normalizedCourseDeleteTitle(typedTitle);
  return Boolean(expected && acknowledged && !busy && typed === expected);
}
