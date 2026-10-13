import api from './http';

const courseProgressInFlight = new Map();


export async function getAssignmentsByCourse(courseId) {
  const res = await api.get(`/api/courses/${courseId}/assignments`);
  return res.data;
}


export async function getCourseProgressByCourses(courseIds) {
  const ids = Array.from(new Set(Array.isArray(courseIds) ? courseIds.filter(Boolean).map(String) : []))
    .sort();
  if (ids.length === 0) return [];

  const requestKey = ids.join('\u001f');
  const existing = courseProgressInFlight.get(requestKey);
  if (existing) return existing;

  const request = api.post('/api/assignments/course-progress', { courseIds: ids })
    .then((res) => res.data)
    .finally(() => {
      if (courseProgressInFlight.get(requestKey) === request) courseProgressInFlight.delete(requestKey);
    });
  courseProgressInFlight.set(requestKey, request);
  return request;
}


export async function getAssignmentRuntimeMeta(assignmentId) {
  const res = await api.get(`/api/assignments/${assignmentId}/runtime-meta`);
  return res.data;
}

export async function getAssignmentEditMeta(assignmentId) {
  const res = await api.get(`/api/assignments/${assignmentId}/edit-meta`);
  return res.data;
}

export async function importAssignmentsFromJson(courseId, graph, options = {}, operationId) {
  const res = await api.post(`/api/courses/${courseId}/assignments/import-json`, { graph, options, operationId });
  return res.data;
}

export async function previewAssignmentsFromJson(courseId, graph, options = {}) {
  const res = await api.post(`/api/courses/${courseId}/assignments/import-json/preview`, { graph, options });
  return res.data;
}

export async function exportAssignmentsToJson(courseId, options = {}) {
  const res = await api.get(`/api/courses/${courseId}/assignments/export-json`, { params: options });
  return res.data;
}


export async function deleteAssignment(assignmentId) {
  const res = await api.delete(`/api/assignments/${assignmentId}`);
  return res.data;
}


export async function updateAssignmentVisibility(assignmentId, payload) {
  const res = await api.patch(`/api/assignments/${assignmentId}/visibility`, payload);
  return res.data;
}


export async function updateAssignmentSort(assignmentId, sort) {
  await api.patch(`/api/assignments/${assignmentId}/sort`, { sort });
}



export async function moveAssignmentAfter(assignmentId, afterAssignmentId) {
  await api.patch(`/api/assignments/${assignmentId}/position`, { afterAssignmentId });
}





export async function addVisibleGroups(courseId, groupIds) {
  const res = await api.post(`/api/courses/${courseId}/visible-groups`, { groupIds });
  return res.data;
}

export async function setCourseOwners(courseId, ownerIds) {
  const res = await api.post(`/api/courses/${courseId}/owners`, { ownerIds });
  return res.data;
}

export async function getAssignmentsByCourseTree(courseId) {
  const res = await api.get(`/api/courses/${courseId}/assignments/tree`);
  return res.data;
}


export async function listTaskGraphImports(courseId) {
  return (await api.get(`/api/courses/${courseId}/assignments/import-json/operations`)).data;
}
export async function getTaskGraphImport(courseId, operationId) {
  return (await api.get(`/api/courses/${courseId}/assignments/import-json/operations/${operationId}`)).data;
}
export async function resumeTaskGraphImport(courseId, operationId) {
  return (await api.post(`/api/courses/${courseId}/assignments/import-json/operations/${operationId}/resume`)).data;
}
export async function saveTaskGraphImportMap(courseId, operationId, expectedVersion, document, replaceConflicted = false) {
  return (await api.put(`/api/courses/${courseId}/assignments/import-json/operations/${operationId}/map`, {
    expectedVersion, document, replaceConflicted,
  })).data;
}

export async function stopTaskGraphImport(courseId, operationId) {
  return (await api.post(`/api/courses/${courseId}/assignments/import-json/operations/${operationId}/stop`, { acknowledgePartial: true })).data;
}
