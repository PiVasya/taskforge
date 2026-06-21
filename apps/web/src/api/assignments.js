import api from './http';




export async function getAssignmentsByCourse(courseId) {
  const res = await api.get(`/api/courses/${courseId}/assignments`);
  return res.data;
}


export async function getCourseProgressByCourses(courseIds) {
  const ids = Array.isArray(courseIds) ? courseIds.filter(Boolean) : [];
  if (ids.length === 0) return [];
  const res = await api.post('/api/assignments/course-progress', { courseIds: ids });
  return res.data;
}


export async function getAssignment(assignmentId) {
  const res = await api.get(`/api/assignments/${assignmentId}`);
  return res.data;
}

export async function getAssignmentForEdit(assignmentId) {
  const res = await api.get(`/api/assignments/${assignmentId}/edit`);
  return res.data;
}


export async function createAssignment(courseId, payload) {
  const res = await api.post(`/api/courses/${courseId}/assignments`, payload);
  return res.data;
}

export async function importAssignmentsFromJson(courseId, payload) {
  const res = await api.post(`/api/courses/${courseId}/assignments/import-json`, payload);
  return res.data;
}

export async function exportAssignmentsToJson(courseId) {
  const res = await api.get(`/api/courses/${courseId}/assignments/export-json`);
  return res.data;
}


export async function updateAssignment(assignmentId, payload) {
  const res = await api.put(`/api/assignments/${assignmentId}`, payload);
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


export async function getTopSolutions(assignmentId, top = 20) {
  const res = await api.get(`/api/assignments/${assignmentId}/top-solutions`, {
    params: { top },
  });
  return res.data;
}



export async function addVisibleGroups(courseId, groupIds) {
  const res = await api.post(`/api/courses/${courseId}/visible-groups`, { groupIds });
  return res.data;
}

export async function setCourseOwners(courseId, ownerIds) {
  const res = await api.post(`/api/courses/${courseId}/owners`, { ownerIds });
  return res.data;
}
