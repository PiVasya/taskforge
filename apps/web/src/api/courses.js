
import api from './http';


export async function getCourses(params = {}) {
  const { data } = await api.get('/api/courses', { params });
  return data; 
}


export async function getCourse(id) {
  const { data } = await api.get(`/api/courses/${id}`);
  return data; 
}
export const getCourseById = getCourse;


export async function createCourse(payload) {
  
  const { data } = await api.post('/api/courses', payload);
  return data; 
}


export async function updateCourse(id, payload) {
  const { data } = await api.put(`/api/courses/${id}`, payload);
  return data;
}


export async function updateCourseSort(courseId, sort) {
  const { data } = await api.patch(`/api/courses/${courseId}/sort`, { sort });
  return data;
}


export async function moveCoursePosition(courseId, parentCourseId, position) {
  const { data } = await api.patch(`/api/courses/${courseId}/position`, {
    parentCourseId: parentCourseId || null,
    position,
  });
  return data;
}


export async function deleteCourse(id) {
  await api.delete(`/api/courses/${id}`);
}


export async function getAssignments(courseId) {
  const { data } = await api.get(`/api/courses/${courseId}/assignments`);
  return data;
}
export async function createAssignment(courseId, payload) {
  const { data } = await api.post(`/api/courses/${courseId}/assignments`, payload);
  return data;
}
