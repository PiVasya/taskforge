export async function captureTaskGraphPreviewSnapshot({ readMap, readExport }) {
  const before = await readMap();
  const currentExport = await readExport();
  const after = await readMap();
  if (Number(before?.version || 0) !== Number(after?.version || 0)) {
    const error = new Error('Карта изменилась во время проверки. Повторите Preview.');
    error.code = 'IMPORT_PREVIEW_CHANGED';
    throw error;
  }
  return { currentExport, mapVersion: Number(after?.version || 0) };
}

// Always display/apply the IDs that the server has reserved for this root course.
// The incoming source file remains untouched; explicit user UUIDs are never replaced.
export function applyServerGraphBindings(parsed, preflight) {
  if (!parsed || typeof parsed !== 'object' || parsed.format !== 'taskforge-task-graph' || !preflight?.bindings) return parsed;
  const apply = (name) => {
    const bindings = new Map((preflight.bindings[name] || []).map(({ key, id }) => [key, id]));
    return (Array.isArray(parsed[name]) ? parsed[name] : []).map((row) => {
      if (row?.id || !bindings.has(row?.key)) return row;
      const id = bindings.get(row.key);
      return typeof id === 'string' && /^[0-9a-f]{8}-[0-9a-f-]{27,}$/i.test(id)
        ? { ...row, id } : row;
    });
  };
  const courses = apply('courses');
  const tasks = apply('tasks');
  const changed = courses.some((row, i) => row !== parsed.courses?.[i])
    || tasks.some((row, i) => row !== parsed.tasks?.[i]);
  return changed ? { ...parsed, scopes: Array.from(new Set([...(parsed.scopes || []), 'ids'])), courses, tasks } : parsed;
}
