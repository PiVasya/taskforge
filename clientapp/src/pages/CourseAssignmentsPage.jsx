import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router-dom';

import Layout from '../components/Layout';
import { Card, Button, Input, Badge } from '../components/ui';

import { getAssignmentsByCourse, createAssignment } from '../api/assignments';
import { Plus, Search, Layers } from 'lucide-react';

export default function CourseAssignmentsPage() {
    const { courseId } = useParams();
    const nav = useNavigate();

    const [items, setItems] = useState([]);
    const [q, setQ] = useState('');
    const [loading, setLoading] = useState(true);
    const [err, setErr] = useState('');

    useEffect(() => {
        (async () => {
            try {
                setLoading(true);
                setErr('');
                const list = await getAssignmentsByCourse(courseId);
                setItems(list);
            } catch (e) {
                setErr(e.message || 'Ошибка загрузки');
            } finally {
                setLoading(false);
            }
        })();
    }, [courseId]);

    const filtered = useMemo(
        () =>
            items.filter(
                (x) =>
                    (x.title || '').toLowerCase().includes(q.toLowerCase()) ||
                    (x.tags || '').toLowerCase().includes(q.toLowerCase())
            ),
        [items, q]
    );

    const handleCreate = async () => {
        const payload = {
            title: 'Новая задача',
            description: '',
            type: 'code-test',
            difficulty: 1,
            testCases: [{ input: '', expectedOutput: '', isHidden: false }],
        };
        const res = await createAssignment(courseId, payload);
        const id = res?.id;
        if (id) nav(`/assignment/${id}/edit`);
    };

    return (
        <Layout>
            <div className="flex items-center justify-between mb-6">
                <h1 className="text-2xl font-semibold flex items-center gap-2">
                    <Layers size={22} /> Задания курса
                </h1>
                <Button onClick={handleCreate}>
                    <Plus size={16} /> Создать
                </Button>
            </div>

            <Card className="mb-6">
                <div className="flex items-center gap-3">
                    <div className="relative flex-1">
                        <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-400" />
                        <Input
                            placeholder="Поиск по названию или тегам"
                            value={q}
                            onChange={(e) => setQ(e.target.value)}
                            className="pl-9"
                        />
                    </div>
                </div>
            </Card>

            {err && <div className="text-red-500 font-medium mb-4">{err}</div>}
            {loading && <div className="text-slate-500">Загрузка…</div>}

            <div className="grid md:grid-cols-2 xl:grid-cols-3 gap-5">
                {filtered.map((a) => (
                    <Card key={a.id} className="hover:shadow-lg transition">
                        <div className="flex items-start justify-between gap-3">
                            <div>
                                <Link to={`/assignment/${a.id}/edit`} className="text-lg font-semibold hover:underline">
                                    {a.title}
                                </Link>
                                {a.description && (
                                    <p className="text-sm text-slate-500 line-clamp-2 mt-1">{a.description}</p>
                                )}
                                <div className="mt-3 flex flex-wrap gap-2">
                                    {a.tags
                                        ?.split(',')
                                        .filter(Boolean)
                                        .map((t) => (
                                            <Badge key={t.trim()}>{t.trim()}</Badge>
                                        ))}
                                </div>
                            </div>
                            <div className="text-right">
                                <div className="text-xs text-slate-500">Сложность</div>
                                <div className="text-sm font-semibold">
                                    {['легко', 'средне', 'сложно'][a.difficulty - 1] || '—'}
                                </div>
                            </div>
                        </div>
                    </Card>
                ))}
            </div>

            {!loading && filtered.length === 0 && (
                <div className="card-muted p-8 text-center text-slate-500 mt-6">
                    Пока заданий нет. Создайте первое ✨
                </div>
            )}
        </Layout>
    );
}
