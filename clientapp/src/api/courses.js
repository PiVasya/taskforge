const API_URL = process.env.REACT_APP_API_URL;

function auth() {
    const token = localStorage.getItem('token');
    return token ? { Authorization: `Bearer ${token}` } : {};
}

// список курсов; scope: 'mine' | 'public' | 'all'
export async function getCourses(scope = 'mine') {
    const res = await fetch(`${API_URL}/api/courses?scope=${encodeURIComponent(scope)}`, {
        headers: { ...auth() }
    });
    if (!res.ok) throw new Error('Не удалось загрузить курсы');
    return await res.json();
}

// создать курс
export async function createCourse({ title, description, isPublic }) {
    const res = await fetch(`${API_URL}/api/courses`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...auth() },
        body: JSON.stringify({ title, description, isPublic: !!isPublic })
    });
    if (!res.ok) {
        const t = await res.text();
        throw new Error(t || 'Не удалось создать курс');
    }
    return await res.json(); // { id } или весь объект — как у тебя сделано
}

// получить один курс
export async function getCourseById(courseId) {
    const res = await fetch(`${API_URL}/api/courses/${courseId}`, {
        headers: { ...auth() }
    });
    if (!res.ok) throw new Error('Курс не найден');
    return await res.json();
}

// обновить курс (для будущего редактирования)
export async function updateCourse(courseId, patch) {
    const res = await fetch(`${API_URL}/api/courses/${courseId}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json', ...auth() },
        body: JSON.stringify(patch)
    });
    if (!res.ok) throw new Error('Не удалось обновить курс');
    return await res.json();
}
