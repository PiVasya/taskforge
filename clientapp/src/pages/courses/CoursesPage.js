import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { getCourses, createCourse } from '../../api/courses';

function CoursesPage() {
    const navigate = useNavigate();

    // список
    const [courses, setCourses] = useState([]);
    const [scope, setScope] = useState('mine'); // 'mine' | 'public' | 'all'
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState('');

    // форма
    const [title, setTitle] = useState('');
    const [description, setDescription] = useState('');
    const [isPublic, setIsPublic] = useState(false);

    // сортировка/поиск (простые)
    const [q, setQ] = useState('');
    const [sort, setSort] = useState('created-desc'); // 'created-desc' | 'created-asc' | 'title-asc'

    useEffect(() => {
        loadCourses();
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [scope]);

    async function loadCourses() {
        try {
            setLoading(true);
            setError('');
            const data = await getCourses(scope);
            setCourses(data);
        } catch (e) {
            setError(e.message || 'Ошибка загрузки');
        } finally {
            setLoading(false);
        }
    }

    async function handleAddCourse() {
        if (!title.trim()) {
            setError('Укажите название курса');
            return;
        }
        try {
            setError('');
            await createCourse({ title: title.trim(), description: description.trim(), isPublic });
            setTitle('');
            setDescription('');
            setIsPublic(false);
            await loadCourses();
        } catch (e) {
            setError(e.message || 'Не удалось создать курс');
        }
    }

    const filtered = useMemo(() => {
        let arr = [...courses];

        if (q.trim()) {
            const needle = q.trim().toLowerCase();
            arr = arr.filter(c =>
                (c.title || '').toLowerCase().includes(needle) ||
                (c.description || '').toLowerCase().includes(needle)
            );
        }

        switch (sort) {
            case 'created-asc':
                arr.sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt));
                break;
            case 'title-asc':
                arr.sort((a, b) => (a.title || '').localeCompare(b.title || ''));
                break;
            default: // created-desc
                arr.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
                break;
        }

        return arr;
    }, [courses, q, sort]);

    return (
        <div style={{ maxWidth: 900, margin: '0 auto', padding: '1rem' }}>
            <h2>Курсы</h2>

            {/* панель фильтров */}
            <div style={{ display: 'flex', gap: 12, alignItems: 'center', marginBottom: 12 }}>
                <select value={scope} onChange={e => setScope(e.target.value)}>
                    <option value="mine">Мои</option>
                    <option value="public">Публичные</option>
                    <option value="all">Все</option>
                </select>

                <input
                    placeholder="Поиск по названию/описанию…"
                    value={q}
                    onChange={e => setQ(e.target.value)}
                    style={{ flex: 1 }}
                />

                <select value={sort} onChange={e => setSort(e.target.value)}>
                    <option value="created-desc">Новые сверху</option>
                    <option value="created-asc">Старые сверху</option>
                    <option value="title-asc">По названию (A→Z)</option>
                </select>

                <button onClick={loadCourses} disabled={loading}>
                    Обновить
                </button>
            </div>

            {/* список */}
            {loading && <div>Загрузка…</div>}
            {error && <div style={{ color: 'crimson' }}>{error}</div>}

            {!loading && filtered.length === 0 && <div>Пока курсов нет</div>}

            <ul style={{ paddingLeft: 18 }}>
                {filtered.map(c => (
                    <li key={c.id} style={{ marginBottom: 8 }}>
                        <div
                            onClick={() => navigate(`/course/${c.id}`)} // ⚠️ убедись, что роут именно /course/:courseId
                            style={{ cursor: 'pointer', color: '#2563EB', fontWeight: 600 }}
                            title="Открыть курс"
                        >
                            {c.title}
                        </div>
                        <div style={{ color: '#555' }}>
                            {(c.description || '').slice(0, 160)}
                            {c.description && c.description.length > 160 ? '…' : ''}
                        </div>
                        <div style={{ fontSize: 12, color: '#777', marginTop: 2 }}>
                            {c.isPublic ? 'Публичный' : 'Приватный'} · {new Date(c.createdAt).toLocaleString()}
                        </div>
                    </li>
                ))}
            </ul>

            <hr style={{ margin: '16px 0' }} />

            {/* форма создания */}
            <h3>Создать курс</h3>
            <div style={{ display: 'grid', gap: 8 }}>
                <label>
                    Название<span style={{ color: 'crimson' }}>*</span>
                    <input
                        value={title}
                        onChange={e => setTitle(e.target.value)}
                        placeholder="Например: Алгоритмы на C#"
                        style={{ width: '100%' }}
                    />
                </label>

                <label>
                    Описание (поддержи Markdown — как текст; предпросмотр ниже)
                    <textarea
                        value={description}
                        onChange={e => setDescription(e.target.value)}
                        rows={6}
                        placeholder="Кратко опиши содержание, цели и требования…"
                        style={{ width: '100%', resize: 'vertical' }}
                    />
                </label>

                <label style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                    <input
                        type="checkbox"
                        checked={isPublic}
                        onChange={e => setIsPublic(e.target.checked)}
                    />
                    Публичный курс
                </label>

                <div>
                    <button onClick={handleAddCourse} disabled={loading}>
                        Создать курс
                    </button>
                </div>
            </div>

            {/* очень простой предпросмотр (без markdown-рендера, но полезно для формы) */}
            {description.trim() && (
                <div style={{ marginTop: 16 }}>
                    <div style={{ fontWeight: 600, marginBottom: 4 }}>Предпросмотр описания:</div>
                    <div
                        style={{
                            border: '1px solid #ddd',
                            background: '#fafafa',
                            padding: 12,
                            whiteSpace: 'pre-wrap'
                        }}
                    >
                        {description}
                    </div>
                </div>
            )}
        </div>
    );
}

export default CoursesPage;
