const API_URL = process.env.REACT_APP_API_URL;

function auth() {
    const t = localStorage.getItem('token');
    return t ? { Authorization: `Bearer ${t}` } : {};
}

// список заданий по курсу
export async function getAssignmentsByCourse(courseId) {
    const r = await fetch(`${API_URL}/api/courses/${courseId}/assignments`, { headers: { ...auth() } });
    if (!r.ok) throw new Error('Не удалось загрузить задания');
    return await r.json();
}

// создать задание
export async function createAssignment(courseId, payload) {
    const r = await fetch(`${API_URL}/api/courses/${courseId}/assignments`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...auth() },
        body: JSON.stringify(payload)
    });
    const t = await r.text();
    if (!r.ok) throw new Error(t || 'Не удалось создать задание');
    return t ? JSON.parse(t) : null; // сервер может вернуть { id }
}

// детальная инфа по заданию
export async function getAssignment(assignmentId) {
    const r = await fetch(`${API_URL}/api/assignments/${assignmentId}`, { headers: { ...auth() } });
    if (!r.ok) throw new Error('Задание не найдено');
    return await r.json();
}

// отправка решения
export async function submitSolution(assignmentId, { language, code }) {
    const r = await fetch(`${API_URL}/api/assignments/${assignmentId}/submit`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...auth() },
        body: JSON.stringify({ language, code })
    });
    const t = await r.text();
    if (!r.ok) throw new Error(t || 'Не удалось отправить решение');
    return t ? JSON.parse(t) : null; // SubmitSolutionResultDto
}

export async function updateAssignment(assignmentId, data) {
    const res = await fetch(`${API_URL}/api/assignments/${assignmentId}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...auth() },
        body: JSON.stringify(data)
    });
    if (!res.ok) throw new Error('Ошибка обновления задания');
    return true;
}

export async function deleteAssignment(assignmentId) {
    const res = await fetch(`${API_URL}/api/assignments/${assignmentId}`, {
        method: 'DELETE',
        headers: { ...auth() }
    });
    if (!res.ok) throw new Error('Ошибка удаления задания');
    return true;
}