import api from './http';

export async function getMainCourses() {
  const res = await api.get('/api/courses');
  return res.data;
}

export async function getMainCourse(id) {
  const res = await api.get(`/api/courses/${encodeURIComponent(id)}`);
  return res.data;
}
