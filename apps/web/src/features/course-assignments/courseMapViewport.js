export function measureCourseMapViewportHeight(element, viewportHeight) {
  if (!element || !Number.isFinite(viewportHeight)) return null;
  const rect = element.getBoundingClientRect();
  if (!rect || !Number.isFinite(rect.top)) return null;
  return Math.max(360, Math.min(1000, Math.floor(viewportHeight - rect.top - 14)));
}
