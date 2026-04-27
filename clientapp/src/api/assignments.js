import api from './http';

// ===== Assignments (задания) =====

// Список заданий конкретного курса
export async function getAssignmentsByCourse(courseId) {
  const res = await api.get(`/api/courses/${courseId}/assignments`);
  return res.data;
}

// Детали задания
export async function getAssignment(assignmentId) {
  const res = await api.get(`/api/assignments/${assignmentId}`);
  return res.data;
}

// Создать задание в курсе
export async function createAssignment(courseId, payload) {
  const res = await api.post(`/api/courses/${courseId}/assignments`, payload);
  return res.data;
}

// Обновить задание
export async function updateAssignment(assignmentId, payload) {
  const res = await api.put(`/api/assignments/${assignmentId}`, payload);
  return res.data;
}

// Удалить задание
export async function deleteAssignment(assignmentId) {
  const res = await api.delete(`/api/assignments/${assignmentId}`);
  return res.data;
}

// Обновить видимость / статус черновика
export async function updateAssignmentVisibility(assignmentId, payload) {
  const res = await api.patch(`/api/assignments/${assignmentId}/visibility`, payload);
  return res.data;
}

// Обновить сортировку
export async function updateAssignmentSort(assignmentId, sort) {
  await api.patch(`/api/assignments/${assignmentId}/sort`, { sort });
}

// Атомарно переместить задание после другого задания.
// afterAssignmentId=null означает переместить в начало курса.
export async function moveAssignmentAfter(assignmentId, afterAssignmentId) {
  await api.patch(`/api/assignments/${assignmentId}/position`, { afterAssignmentId });
}

// Топ лучших решений (для страницы "Топ решений")
export async function getTopSolutions(assignmentId, top = 20) {
  const res = await api.get(`/api/assignments/${assignmentId}/top-solutions`, {
    params: { top },
  });
  return res.data;
}

// ===== Course visibility / owners (используется в настройках курса) =====

export async function addVisibleGroups(courseId, groupIds) {
  const res = await api.post(`/api/courses/${courseId}/visible-groups`, { groupIds });
  return res.data;
}

export async function setCourseOwners(courseId, ownerIds) {
  const res = await api.post(`/api/courses/${courseId}/owners`, { ownerIds });
  return res.data;
}
