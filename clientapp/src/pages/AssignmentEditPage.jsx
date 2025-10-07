import React, { useEffect, useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router-dom';

// Если у тебя уже есть эти компоненты — оставь импорт.
// Если нет — временно убери их и верни обычные <div>/<input> (я показал классы tailwind).
import Layout from '../components/Layout';
import { Field, Input, Textarea, Select, Button, Card } from '../components/ui';

import { getAssignment, updateAssignment, deleteAssignment } from '../api/assignments';
import { PlusCircle, Trash2, Save, ArrowLeft } from 'lucide-react';

export default function AssignmentEditPage() {
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

    const [courseId, setCourseId] = useState(null);

    useEffect(() => {
        (async () => {
            try {
                setError('');
                const a = await getAssignment(assignmentId);
                setTitle(a.title || '');
                setDescription(a.description || '');
                setType(a.type || 'code-test');
                setTags(a.tags || '');
                setDifficulty(a.difficulty || 1);
                setTestCases((a.testCases || []).map(t => ({
                    id: t.id,
                    input: t.input || '',
                    expectedOutput: t.expectedOutput || '',
                    isHidden: !!t.isHidden
                })));
                setCourseId(a.courseId || null);
            } catch (e) {
                setError(e.message || 'Ошибка загрузки задания');
            } finally {
                setLoading(false);
            }
        })();
    }, [assignmentId]);

    const addTest = () => setTestCases(prev => [...prev, { input: '', expectedOutput: '', isHidden: false }]);
    const removeTest = (idx) => setTestCases(prev => prev.filter((_, i) => i !== idx));
    const changeTest = (idx, field, value) => {
        setTestCases(prev => {
            const next = [...prev];
            next[idx] = { ...next[idx], [field]: value };
            return next;
        });
    };

    const handleSave = async () => {
        setSaving(true);
        setError('');
        try {
            await updateAssignment(assignmentId, {
                title,
                description,
                type,
                tags,
                difficulty: Number(difficulty),
                testCases: testCases.map(t => ({
                    id: t.id, // игнорится на бэке при replace-all — ок
                    input: t.input,
                    expectedOutput: t.expectedOutput,
                    isHidden: !!t.isHidden
                }))
            });
            if (courseId) nav(`/course/${courseId}`);
        } catch (e) {
            setError(e.message || 'Ошибка обновления задания');
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
            setError(e.message || 'Ошибка удаления задания');
        }
    };

    if (loading) return <Layout><div className="text-slate-500">Загрузка…</div></Layout>;

    return (
        <Layout>
            {courseId && (
                <Link to={`/course/${courseId}`} className="inline-flex items-center gap-2 text-brand-600 hover:underline mb-5">
                    <ArrowLeft size={16} /> к заданиям курса
                </Link>
            )}

            {error && <div className="text-red-500 font-medium mb-4">{error}</div>}

            <div className="grid lg:grid-cols-3 gap-6">
                <div className="lg:col-span-2 space-y-5">
                    <Card>
                        <h2 className="text-xl font-semibold mb-4">Основное</h2>
                        <div className="grid sm:grid-cols-2 gap-4">
                            <Field label="Название"><Input value={title} onChange={e => setTitle(e.target.value)} /></Field>
                            <Field label="Тип">
                                <Select value={type} onChange={e => setType(e.target.value)}>
                                    <option value="code-test">code-test</option>
                                    <option value="quiz" disabled>quiz (скоро)</option>
                                </Select>
                            </Field>
                            <Field label="Сложность">
                                <Select value={difficulty} onChange={e => setDifficulty(e.target.value)}>
                                    <option value={1}>легко</option>
                                    <option value={2}>средне</option>
                                    <option value={3}>сложно</option>
                                </Select>
                            </Field>
                            <Field label="Теги (через запятую)"><Input value={tags} onChange={e => setTags(e.target.value)} /></Field>
                            <div className="sm:col-span-2">
                                <Field label="Описание (markdown/html)">
                                    <Textarea rows={10} value={description} onChange={e => setDescription(e.target.value)} />
                                </Field>
                            </div>
                        </div>
                    </Card>

                    <Card>
                        <div className="flex items-center justify-between mb-3">
                            <h2 className="text-xl font-semibold">Тест-кейсы</h2>
                            <Button className="btn-outline" onClick={addTest}><PlusCircle size={16} /> Добавить тест</Button>
                        </div>

                        <div className="space-y-4">
                            {testCases.map((t, idx) => (
                                <div key={idx} className="rounded-xl border border-slate-200 dark:border-slate-800 p-4 bg-white/60 dark:bg-slate-900/40">
                                    <div className="grid sm:grid-cols-2 gap-4">
                                        <Field label="Input">
                                            <Textarea rows={4} value={t.input} onChange={e => changeTest(idx, 'input', e.target.value)} />
                                        </Field>
                                        <Field label="Expected Output">
                                            <Textarea rows={4} value={t.expectedOutput} onChange={e => changeTest(idx, 'expectedOutput', e.target.value)} />
                                        </Field>
                                        <label className="flex items-center gap-2 text-sm sm:col-span-2">
                                            <input type="checkbox" checked={t.isHidden} onChange={e => changeTest(idx, 'isHidden', e.target.checked)} />
                                            Скрытый
                                        </label>
                                    </div>
                                    <div className="mt-3 flex justify-end">
                                        <Button variant="outline" className="text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20" onClick={() => removeTest(idx)}>
                                            <Trash2 size={16} /> Удалить тест
                                        </Button>
                                    </div>
                                </div>
                            ))}
                        </div>
                    </Card>
                </div>

                <div className="space-y-4">
                    <Card>
                        <div className="flex gap-2">
                            <Button onClick={handleSave} disabled={saving} className="flex-1">
                                <Save size={16} /> {saving ? 'Сохраняю…' : 'Сохранить'}
                            </Button>
                            <Button variant="outline" onClick={handleDelete} className="flex-1 text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20">
                                <Trash2 size={16} /> Удалить
                            </Button>
                        </div>
                    </Card>

                    <Card>
                        <div className="text-sm text-slate-500">Подсказка: используйте публичные и скрытые тесты, чтобы проверки были надёжными.</div>
                    </Card>
                </div>
            </div>
        </Layout>
    );
}
