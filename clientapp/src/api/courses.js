import { api } from "./http";

// Список курсов
export async function getCourses() {
    const { data } = await api.get("/api/courses");
    return data;
}

// Детали курса (современное имя)
export async function getCourse(id) {
    const { data } = await api.get(`/api/courses/${id}`);
    return data;
}

// Алиас под старое имя в проекте
export async function getCourseById(id) {
    return getCourse(id);
}

// Создать курс
export async function createCourse(payload) {
    const { data } = await api.post("/api/courses", payload);
    return data; // { id }
}

// Обновить курс
export async function updateCourse(id, payload) {
    await api.put(`/api/courses/${id}`, payload);
}

// Удалить курс (могло не быть — добавляем)
export async function deleteCourse(id) {
    await api.delete(`/api/courses/${id}`);
}

/* Доп. алиасы, если где-то используются: */
export async function getAssignments(courseId) {
    const { data } = await api.get(`/api/courses/${courseId}/assignments`);
    return data;
}
export async function createAssignment(courseId, payload) {
    const { data } = await api.post(`/api/courses/${courseId}/assignments`, payload);
    return data;
}
