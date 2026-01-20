import api from './http';

export async function listAssignments(courseId) {
  const res = await api.get(`/api/courses/${courseId}/assignments`);
  return res.data;
}

export async function createAssignment(courseId, payload) {
  const res = await api.post(`/api/courses/${courseId}/assignments`, payload);
  return res.data;
}

export async function updateAssignment(courseId, assignmentId, payload) {
  const res = await api.put(`/api/courses/${courseId}/assignments/${assignmentId}`, payload);
  return res.data;
}

export async function deleteAssignment(courseId, assignmentId) {
  const res = await api.delete(`/api/courses/${courseId}/assignments/${assignmentId}`);
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
