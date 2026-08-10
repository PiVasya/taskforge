import api from './http';

export async function getCourseMap(courseId) {
  const res = await api.get(`/api/courses/${courseId}/map`);
  return res.data;
}

export async function getLearningCourseMap(courseId) {
  const res = await api.get(`/api/courses/${courseId}/learning-map`);
  return res.data;
}

export async function saveCourseMap(courseId, expectedVersion, document) {
  const res = await api.put(`/api/courses/${courseId}/map`, {
    expectedVersion,
    document,
  });
  return res.data;
}
