import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router-dom';

import Layout from '../components/Layout';
import { Card, Button, Input, Badge } from '../components/ui';

import { getAssignmentsByCourse, createAssignment } from '../api/assignments';
import { Plus, Layers, CheckCircle2 } from 'lucide-react';
import IfEditor from '../components/IfEditor';

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
            description: 'Опишите постановку задачи…',
            type: 'code-test',
            difficulty: 1,
            testCases: [{ input: '2 4', expectedOutput: '6', isHidden: false }],
            tags: '',
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

                <IfEditor>
                    <Button onClick={handleCreate}>
                        <Plus size={16} /> Создать
                    </Button>
                </IfEditor>
            </div>

            <Card className="mb-6">
                <div className="flex items-center gap-3">
                    <div className="relative flex-1">
                        <Input
                            placeholder="Поиск по названию или тегам"
                            value={q}
                            onChange={(e) => setQ(e.target.value)}
                        />
                    </div>
                </div>
            </Card>

            {err && <div className="text-red-500 font-medium mb-4">{err}</div>}
            {loading && <div className="text-slate-500">Загрузка…</div>}

            <div className="grid md:grid-cols-2 xl:grid-cols-3 gap-5">
                {filtered.map((a) => {
                    const solved = !!a.solvedByCurrentUser;

                    // ССЫЛКА НА ВСЮ КАРТОЧКУ:
                    const ViewWrap = ({ children }) => (
                        <Link to={`/assignment/${a.id}`} className="block group">
                            {children}
                        </Link>
                    );
                    const EditWrap = ({ children }) => (
                        <Link to={`/assignment/${a.id}/edit`} className="block group">
                            {children}
                        </Link>
                    );

                    const CardInner = (
                        <Card
                            className={
                                'transition hover:shadow-lg ' +
                                (solved ? 'border-emerald-400/40 bg-emerald-500/5' : '')
                            }
                        >
                            {/* Верхняя строка: заголовок + бейдж слева, «Сложность» справа */}
                            <div className="flex items-start justify-between gap-4">
                                {/* Левая колонка — текст, не даём ей «налезать» на правую */}
                                <div className="min-w-0 grow">
                                    <div className="flex items-center gap-2">
                                        <div
                                            className={
                                                'text-lg font-semibold truncate ' +
                                                (solved ? 'text-emerald-600' : '')
                                            }
                                            title={a.title}
                                        >
                                            {a.title}
                                        </div>
                                        {solved && (
                                            <span className="inline-flex items-center gap-1 rounded-full border border-emerald-400/40 bg-emerald-500/10 px-2 py-0.5 text-emerald-600 text-xs">
                                                <CheckCircle2 size={14} />
                                                Решено
                                            </span>
                                        )}
                                    </div>

                                    {a.description && (
                                        <p className="text-sm text-slate-500 line-clamp-2 mt-1">
                                            {a.description}
                                        </p>
                                    )}

                                    <div className="mt-3 flex flex-wrap gap-2">
                                        {a.tags?.split(',').filter(Boolean).map((t) => (
                                            <Badge key={t.trim()}>{t.trim()}</Badge>
                                        ))}
                                    </div>
                                </div>

                                {/* Правая колонка фиксированной ширины — не даём ей схлопываться */}
                                <div className="shrink-0 w-28 text-right">
                                    <div className="text-xs text-slate-500">Сложность</div>
                                    <div className="text-sm font-semibold">
                                        {['легко', 'средне', 'сложно'][a.difficulty - 1] || '—'}
                                    </div>
                                </div>
                            </div>
                        </Card>
                    );

                    return (
                        <IfEditor key={a.id} otherwise={<ViewWrap>{CardInner}</ViewWrap>}>
                            <EditWrap>{CardInner}</EditWrap>
                        </IfEditor>
                    );
                })}
            </div>

            {!loading && filtered.length === 0 && (
                <div className="card-muted p-8 text-center text-slate-500 mt-6">
                    Пока заданий нет. Создайте первое ✨
                </div>
            )}
        </Layout>
    );
}
