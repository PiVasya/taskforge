import React, { useEffect, useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router-dom';
import { getCourseById, updateCourse } from '../../api/courses';

function CourseEditPage() {
    const { courseId } = useParams();
    const nav = useNavigate();

    const [loading, setLoading] = useState(true);
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState('');

    const [title, setTitle] = useState('');
    const [description, setDescription] = useState('');
    const [isPublic, setIsPublic] = useState(false);

    useEffect(() => {
        (async () => {
            try {
                const c = await getCourseById(courseId);
                setTitle(c.title || '');
                setDescription(c.description || '');
                setIsPublic(!!c.isPublic);
            } catch (e) {
                setError(e.message);
            } finally {
                setLoading(false);
            }
        })();
    }, [courseId]);

    const handleSave = async () => {
        setError('');
        setSaving(true);
        try {
            await updateCourse(courseId, { title, description, isPublic });
            nav(`/course/${courseId}`); // назад на страницу курса
        } catch (e) {
            setError(e.message);
        } finally {
            setSaving(false);
        }
    };

    if (loading) return <p>Загрузка…</p>;

    return (
        <div style={{ maxWidth: 800, margin: '0 auto' }}>
            <h2>Редактирование курса</h2>
            <p><Link to={`/course/${courseId}`}>&larr; к заданиям курса</Link></p>

            {error && <p style={{ color: 'red' }}>{error}</p>}

            <div>
                <label>Название<br />
                    <input value={title} onChange={e => setTitle(e.target.value)} />
                </label>
            </div>
            <div>
                <label>Описание (markdown/html)<br />
                    <textarea rows={6} value={description} onChange={e => setDescription(e.target.value)} />
                </label>
            </div>
            <div>
                <label>
                    <input type="checkbox" checked={isPublic} onChange={e => setIsPublic(e.target.checked)} />
                    &nbsp;Публичный курс
                </label>
            </div>

            <button disabled={saving} onClick={handleSave}>
                {saving ? 'Сохраняю…' : 'Сохранить'}
            </button>
        </div>
    );
}

export default CourseEditPage;
