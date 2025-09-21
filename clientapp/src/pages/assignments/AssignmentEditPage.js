import React, { useEffect, useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router-dom';
import { getAssignment, updateAssignment, deleteAssignment } from '../../api/assignments';

function AssignmentEditPage() {
    const { assignmentId } = useParams();
    const nav = useNavigate();

    const [loading, setLoading] = useState(true);
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState('');

    const [title, setTitle] = useState('');
    const [description, setDescription] = useState('');
    const [type, setType] = useState('code-test');
    const [tags, setTags] = useState('');
    const [difficulty, setDifficulty] = useState(1);
    const [testCases, setTestCases] = useState([]);

    const [courseId, setCourseId] = useState(null); // чтобы вернуться к курсу

    useEffect(() => {
        (async () => {
            try {
                const a = await getAssignmentById(assignmentId);
                setTitle(a.title || '');
                setDescription(a.description || '');
                setType(a.type || 'code-test');
                setTags(a.tags || '');
                setDifficulty(a.difficulty || 1);
                setTestCases((a.testCases || []).map(t => ({
                    id: t.id,
                    input: t.input || '',
                    expectedOutput: t.expectedOutput || '',
                    isHidden: !!t.isHidden,
                })));
                setCourseId(a.courseId || null);
            } catch (e) {
                setError(e.message);
            } finally {
                setLoading(false);
            }
        })();
    }, [assignmentId]);

    const addTest = () => setTestCases([...testCases, { input: '', expectedOutput: '', isHidden: false }]);
    const removeTest = (idx) => setTestCases(testCases.filter((_, i) => i !== idx));
    const changeTest = (idx, field, value) => {
        const next = [...testCases];
        next[idx] = { ...next[idx], [field]: value };
        setTestCases(next);
    };

    const handleSave = async () => {
        setError('');
        setSaving(true);
        try {
            await updateAssignment(assignmentId, {
                title,
                description,
                type,
                tags,
                difficulty: Number(difficulty),
                testCases: testCases.map(t => ({
                    id: t.id, // сервер не использует при replace-all, но пусть будет
                    input: t.input,
                    expectedOutput: t.expectedOutput,
                    isHidden: !!t.isHidden
                }))
            });
            if (courseId) nav(`/course/${courseId}`);
        } catch (e) {
            setError(e.message);
        } finally {
            setSaving(false);
        }
    };

    const handleDelete = async () => {
        if (!window.confirm('Удалить задание?')) return;
        try {
            await deleteAssignment(assignmentId);
            if (courseId) nav(`/course/${courseId}`);
            else nav('/courses');
        } catch (e) {
            setError(e.message);
        }
    };

    if (loading) return <p>Загрузка…</p>;

    return (
        <div style={{ maxWidth: 900, margin: '0 auto' }}>
            <h2>Редактирование задания</h2>
            {courseId && <p><Link to={`/course/${courseId}`}>&larr; к заданиям курса</Link></p>}
            {error && <p style={{ color: 'red' }}>{error}</p>}

            <div>
                <label>Название<br />
                    <input value={title} onChange={e => setTitle(e.target.value)} />
                </label>
            </div>
            <div>
                <label>Описание (markdown/html)<br />
                    <textarea rows={10} value={description} onChange={e => setDescription(e.target.value)} />
                </label>
            </div>
            <div>
                <label>Тип&nbsp;
                    <select value={type} onChange={e => setType(e.target.value)}>
                        <option value="code-test">code-test</option>
                        <option value="quiz" disabled>quiz (скоро)</option>
                    </select>
                </label>
            </div>
            <div>
                <label>Сложность&nbsp;
                    <select value={difficulty} onChange={e => setDifficulty(e.target.value)}>
                        <option value={1}>легко</option>
                        <option value={2}>средне</option>
                        <option value={3}>сложно</option>
                    </select>
                </label>
            </div>
            <div>
                <label>Теги (через запятую)<br />
                    <input value={tags} onChange={e => setTags(e.target.value)} />
                </label>
            </div>

            <h3>Тест-кейсы</h3>
            {testCases.map((t, idx) => (
                <div key={idx} style={{ border: '1px solid #ddd', padding: 8, marginBottom: 8 }}>
                    <div>
                        <label>Input<br />
                            <textarea rows={3} value={t.input} onChange={e => changeTest(idx, 'input', e.target.value)} />
                        </label>
                    </div>
                    <div>
                        <label>Expected Output<br />
                            <textarea rows={3} value={t.expectedOutput} onChange={e => changeTest(idx, 'expectedOutput', e.target.value)} />
                        </label>
                    </div>
                    <div>
                        <label>
                            <input type="checkbox" checked={t.isHidden} onChange={e => changeTest(idx, 'isHidden', e.target.checked)} />
                            &nbsp;Скрытый
                        </label>
                    </div>
                    <button onClick={() => removeTest(idx)}>Удалить тест</button>
                </div>
            ))}
            <button onClick={addTest}>+ Добавить тест</button>

            <div style={{ marginTop: 16 }}>
                <button disabled={saving} onClick={handleSave}>{saving ? 'Сохраняю…' : 'Сохранить'}</button>
                <button style={{ marginLeft: 8 }} onClick={handleDelete}>Удалить задание</button>
            </div>
        </div>
    );
}

export default AssignmentEditPage;
