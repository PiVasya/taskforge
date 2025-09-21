import React, { useEffect, useState } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import { getCourses, createCourse } from '../../api/courses';

function CoursesPage() {
    const navigate = useNavigate();
    const [courses, setCourses] = useState([]);
    const [title, setTitle] = useState('');

    useEffect(() => { load(); }, []);
    async function load() { setCourses(await getCourses()); }

    async function handleAddCourse() {
        if (!title) return;
        await createCourse({ title, description: 'Тестовый курс' });
        setTitle('');
        load();
    }

    return (
        <div>
            <h2>Курсы</h2>
            <ul>
                {courses.map(c => (
                    <li key={c.id}>
                        <span
                            style={{ cursor: 'pointer', color: 'blue' }}
                            onClick={() => navigate(`/course/${c.id}`)}
                            title="Открыть курс"
                        >
                            {c.title} — {c.description}
                        </span>
                        {' '}
                        <Link to={`/courses/${c.id}/edit`}>(редактировать)</Link>
                    </li>
                ))}
            </ul>

            <h3>Создать курс</h3>
            <input value={title} onChange={e => setTitle(e.target.value)} placeholder="Название курса" />
            <button onClick={handleAddCourse}>Создать курс</button>
        </div>
    );
}

export default CoursesPage;
