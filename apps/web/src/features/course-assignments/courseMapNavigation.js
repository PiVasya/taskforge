export function navigateToCourseEditor(nav, location, rootCourseId, courseId) {
  const rootId = String(rootCourseId || '').trim();
  const targetId = String(courseId || '').trim();
  if (!rootId || !targetId) return;
  const returnTo = `/course/${rootId}`;
  nav(`/courses/${targetId}/edit?returnTo=${encodeURIComponent(returnTo)}`, {
    state: { backgroundLocation: location, courseMapOverlay: true },
  });
}
