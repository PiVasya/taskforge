import { api } from './http';

// список курсов
export async function getCourses() {
  const { data } = await api.get('/api/courses');
  return data;
}

// детали курса
export async function getCourse(id) {
  const { data } = await api.get(`/api/courses/${id}`);
  return data;
}

// алиас
export async function getCourseById(id) {
  return getCourse(id);
}

// создать курс
export async function createCourse(payload) {
  const { data } = await api.post('/api/courses', payload);
  return data; // { id }
}

// обновить курс
export async function updateCourse(id, payload) {
  await api.put(`/api/courses/${id}`, payload);
}

// удалить курс
export async function deleteCourse(id) {
  await api.delete(`/api/courses/${id}`);
}

// задания курса (алиасы, если используются)
export async function getAssignments(courseId) {
  const { data } = await api.get(`/api/courses/${courseId}/assignments`);
  return data;
}

export async function createAssignment(courseId, payload) {
  const { data } = await api.post(`/api/courses/${courseId}/assignments`, payload);
  return data;
}
