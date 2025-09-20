/*const API_URL = process.env.REACT_APP_API_URL;

export async function getAssignments(courseId) {
    const token = localStorage.getItem('token');
    const response = await fetch(`${API_URL}/api/assignments/course/${courseId}`, {
        headers: { 'Authorization': `Bearer ${token}` }
    });
    return await response.json();
}

export async function createAssignment(courseId, data) {
    const token = localStorage.getItem('token');
    const response = await fetch(`${API_URL}/api/assignments/course/${courseId}`, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify(data)
    });
    return await response.json();
}
*/