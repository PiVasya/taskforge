const API_URL = process.env.REACT_APP_API_URL;

function auth() {
    const token = localStorage.getItem('token');
    return token ? { Authorization: `Bearer ${token}` } : {};
}

export async function getCourses() {
    const res = await fetch(`${API_URL}/api/courses`, { headers: { ...auth() } });
    if (!res.ok) throw new Error('Ошибка загрузки курсов');
    return await res.json();
}

export async function createCourse(data) {
    const res = await fetch(`${API_URL}/api/courses`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...auth() },
        body: JSON.stringify(data)
    });
    if (!res.ok) throw new Error('Ошибка создания курса');
    return await res.json();
}

export async function getCourseById(courseId) {
    const res = await fetch(`${API_URL}/api/courses/${courseId}`, { headers: { ...auth() } });
    if (!res.ok) throw new Error('Курс не найден');
    return await res.json();
}

export async function updateCourse(courseId, data) {
    const res = await fetch(`${API_URL}/api/courses/${courseId}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...auth() },
        body: JSON.stringify(data)
    });
    if (!res.ok) throw new Error('Ошибка обновления курса');
    return true;
}

// assignments внутри курса
export async function getAssignments(courseId) {
    const res = await fetch(`${API_URL}/api/courses/${courseId}/assignments`, { headers: { ...auth() } });
    if (!res.ok) throw new Error('Ошибка загрузки заданий');
    return await res.json();
}

export async function createAssignment(courseId, data) {
    const res = await fetch(`${API_URL}/api/courses/${courseId}/assignments`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...auth() },
        body: JSON.stringify(data)
    });
    if (!res.ok) throw new Error('Ошибка создания задания');
    return await res.json();
}
