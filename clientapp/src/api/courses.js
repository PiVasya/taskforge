// clientapp/src/api/courses.js
import { api } from './http';

// список курсов
export async function getCourses() {
  const { data } = await api.get('/api/courses');
  return data; // CourseListItemDto[]
}

// детали курса
export async function getCourse(id) {
  const { data } = await api.get(`/api/courses/${id}`);
  return data; // CourseDetailsDto
}
export const getCourseById = getCourse;

// создать курс (ВАЖНО: description обязателен)
export async function createCourse(payload) {
  // payload: { title, description, isPublic }
  const { data } = await api.post('/api/courses', payload);
  return data; // { id }
}

// обновить
export async function updateCourse(id, payload) {
  await api.put(`/api/courses/${id}`, payload);
}

// удалить
export async function deleteCourse(id) {
  await api.delete(`/api/courses/${id}`);
}

// задания курса
export async function getAssignments(courseId) {
  const { data } = await api.get(`/api/courses/${courseId}/assignments`);
  return data;
}
export async function createAssignment(courseId, payload) {
  const { data } = await api.post(`/api/courses/${courseId}/assignments`, payload);
  return data;
}
