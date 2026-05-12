
import api from './http';


export async function getCourses() {
  const { data } = await api.get('/api/courses');
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
  await api.put(`/api/courses/${id}`, payload);
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
