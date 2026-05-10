import api from './http';
export async function getLearningCourseTree({ includeDraft = false } = {}) { const res = await api.get('/api/learning/courses/tree', { params: { includeDraft } }); return res.data; }
export async function getLearningCourseOutline(slug, { includeDraft = false } = {}) { const res = await api.get(`/api/learning/courses/${encodeURIComponent(slug)}/outline`, { params: { includeDraft } }); return res.data; }
export async function getCourseConspects(slug, { includeDraft = false } = {}) { const res = await api.get(`/api/learning/courses/${encodeURIComponent(slug)}/conspects`, { params: { includeDraft } }); return res.data; }
export async function getLearningConspects(params = {}) { const res = await api.get('/api/learning/conspects', { params }); return res.data; }
export async function getLearningConspect(idOrSlug, params = {}) { const res = await api.get(`/api/learning/conspects/${encodeURIComponent(idOrSlug)}`, { params }); return res.data; }
export async function createLearningCourse(payload) { const res = await api.post('/api/admin/learning/courses', payload); return res.data; }
export async function updateLearningCourse(courseId, payload) { const res = await api.put(`/api/admin/learning/courses/${courseId}`, payload); return res.data; }
export async function createLearningPage(courseId, payload) { const res = await api.post(`/api/admin/learning/courses/${courseId}/pages`, payload); return res.data; }
export async function createLearningConspect(courseId, payload) { const res = await api.post(`/api/admin/learning/courses/${courseId}/conspects`, payload); return res.data; }
export async function updateLearningConspect(conspectId, payload) { const res = await api.put(`/api/admin/learning/conspects/${conspectId}`, payload); return res.data; }
export async function createLearningConspectTaskLink(conspectId, payload) { const res = await api.post(`/api/admin/learning/conspects/${conspectId}/task-links`, payload); return res.data; }
export async function createLearningTaskLink(courseId, payload) { const res = await api.post(`/api/admin/learning/courses/${courseId}/task-links`, payload); return res.data; }
