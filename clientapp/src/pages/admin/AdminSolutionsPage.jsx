import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Card, Button, Input, Select, Badge } from '../../components/ui';
import {
  searchUsersOnce,
  getUserSolutions,
  getSolutionDetails,
  getSolutionsDetailsBulkOrFallback,
  deleteUserSolutions,
  deleteUser,
} from '../../api/admin';
import { getUserTaskTestAttempts, getAdminTaskTestAttemptReview } from '../../api/taskTestAttempts';
import CodeEditor from '../../components/CodeEditor';

const FILTER_OPTIONS = [
  { label: 'За всё время', value: null },
  { label: 'За сегодня', value: 1 },
  { label: 'За неделю', value: 7 },
  { label: 'За месяц', value: 30 },
  { label: 'Как можно больше', value: 1000 },
];

export default function AdminSolutionsPage() {
  const [tab, setTab] = useState('code'); // 'code' | 'tests'

  const [q, setQ] = useState('');
  const [users, setUsers] = useState([]);
  const [userId, setUserId] = useState('');
  const [searchLoading, setSearchLoading] = useState(false);

  const [solutions, setSolutions] = useState([]);
  const [listLoading, setListLoading] = useState(false);
  const [filterDays, setFilterDays] = useState(null);

  const [detailsMap, setDetailsMap] = useState({});
  const [expandedId, setExpandedId] = useState(null);

  const [testAttempts, setTestAttempts] = useState([]);
  const [testListLoading, setTestListLoading] = useState(false);
  const [testDetailsMap, setTestDetailsMap] = useState({});
  const [expandedTestAttemptId, setExpandedTestAttemptId] = useState(null);

  const loadUsers = async () => {
    setSearchLoading(true);
    try {
      const data = await searchUsersOnce(q, 20);
      setUsers(data || []);
    } catch (e) {
      console.error('Failed to search users', e);
    } finally {
      setSearchLoading(false);
    }
  };

  const loadSolutions = async () => {
    if (!userId) {
      setSolutions([]);
      return;
    }
    setListLoading(true);
    try {
      const data = await getUserSolutions(userId, { days: filterDays });
      setSolutions(Array.isArray(data) ? data : []);
      setExpandedId(null);
      setDetailsMap({});
    } catch (e) {
      console.error('Failed to load solutions', e);
    } finally {
      setListLoading(false);
    }
  };

  const loadTestAttempts = async () => {
    if (!userId) {
      setTestAttempts([]);
      return;
    }
    setTestListLoading(true);
    try {
      const data = await getUserTaskTestAttempts(userId, { days: filterDays });
      setTestAttempts(Array.isArray(data) ? data : []);
      setExpandedTestAttemptId(null);
      setTestDetailsMap({});
    } catch (e) {
      console.error('Failed to load test attempts', e);
    } finally {
      setTestListLoading(false);
    }
  };

  useEffect(() => {
    if (userId) {
      loadSolutions();
      loadTestAttempts();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterDays, userId]);

  const displayedSolutions = useMemo(() => {
    const list = [...solutions];
    if (filterDays) {
      const since = new Date();
      since.setDate(since.getDate() - filterDays);
      // на всякий случай можно было бы фильтровать тут, но мы уже фильтруем на бэке
    }
    list.sort((a, b) => new Date(b.submittedAt) - new Date(a.submittedAt));
    return list;
  }, [solutions, filterDays]);

  const displayedAttempts = useMemo(() => {
    const list = [...testAttempts];
    list.sort((a, b) => new Date(b.submittedAt) - new Date(a.submittedAt));
    return list;
  }, [testAttempts]);

  const splitFillPrompt = (prompt) => {
    const p = String(prompt || '');
    // 3+ чтобы не ловить _ в идентификаторах кода.
    const m = p.match(/_{3,}/);
    if (!m) return null;
    const blank = m[0];
    const i = p.indexOf(blank);
    return { before: p.slice(0, i), after: p.slice(i + blank.length), blankLen: blank.length };
  };

  const handleToggleCode = async (id) => {
    if (expandedId === id) {
      setExpandedId(null);
      return;
    }

    if (!detailsMap[id]) {
      try {
        const dto = await getSolutionDetails(id);
        setDetailsMap((prev) => ({ ...prev, [id]: dto }));
      } catch (e) {
        console.error('Failed to load solution details', e);
        return;
      }
    }

    setExpandedId(id);
  };

  const handleToggleTestAttempt = async (attempt) => {
    const id = attempt.attemptId;
    if (expandedTestAttemptId === id) {
      setExpandedTestAttemptId(null);
      return;
    }

    if (!testDetailsMap[id]) {
      try {
        const dto = await getAdminTaskTestAttemptReview(id);
        setTestDetailsMap((prev) => ({ ...prev, [id]: dto }));
      } catch (e) {
        console.error('Failed to load test attempt review', e);
        return;
      }
    }

    setExpandedTestAttemptId(id);
  };

  const renderAttemptReview = (dto) => {
    if (!dto) return null;
    const qs = Array.isArray(dto.questions) ? dto.questions : [];

    return (
      <div className="mt-4 space-y-4">
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <Badge intent={dto.passed ? 'success' : 'danger'}>
            {dto.passed ? 'Зачёт' : 'Не зачтено'}
          </Badge>
          <Badge intent="secondary">
            {dto.scorePercent}% • {dto.correctQuestions}/{dto.totalQuestions}
          </Badge>
          {dto.timeExpired ? <Badge intent="danger">Время вышло</Badge> : null}
        </div>

        <div className="space-y-4">
          {qs.map((q, i) => {
            const type = String(q.type || '').toLowerCase();
            const isCorrect = !!q.isCorrect;
            const user = q.userAnswer || {};

            const split = type === 'fill' ? splitFillPrompt(q.prompt) : null;
            const userText = (user.text || '').toString();

            const selected = new Set(Array.isArray(user.selectedOptionKeys) ? user.selectedOptionKeys : []);
            const correctKeys = new Set(Array.isArray(q.correctOptionKeys) ? q.correctOptionKeys : []);
            const options = Array.isArray(q.options) ? q.options : [];

            return (
              <div key={q.id || i} className="rounded-xl border border-slate-200 dark:border-slate-700 p-4 bg-[rgb(var(--card))]">
                <div className="flex items-start justify-between gap-3">
                  <div className="font-medium">
                    {i + 1}.{' '}
                    {split ? (
                      <span className="fill-line">
                        {split.before}
                        <input
                          className="fill-input"
                          value={userText}
                          readOnly
                          style={{ width: `${Math.min(30, Math.max(6, (split.blankLen || 3) * 2))}ch` }}
                        />
                        {split.after}
                      </span>
                    ) : (
                      <span>{q.prompt}</span>
                    )}
                  </div>
                  <Badge intent={isCorrect ? 'success' : 'danger'}>
                    {isCorrect ? 'Верно' : 'Неверно'}
                  </Badge>
                </div>

                {(type === 'single-choice' || type === 'multi-choice') && (
                  <div className="mt-3 space-y-2">
                    {options.map((o) => {
                      const isSel = selected.has(o.key);
                      const isCorr = correctKeys.has(o.key);
                      const cls = [
                        'flex items-center gap-2 text-sm',
                        isCorr ? 'text-emerald-700 dark:text-emerald-300 font-medium' : '',
                        isSel && !isCorr ? 'text-rose-700 dark:text-rose-300' : '',
                      ].filter(Boolean).join(' ');

                      return (
                        <div key={o.key} className={cls}>
                          <input type={type === 'multi-choice' ? 'checkbox' : 'radio'} checked={isSel} readOnly />
                          <span>{o.text}</span>
                          {isCorr ? <span className="text-xs opacity-80">(правильный)</span> : null}
                          {isSel && !isCorr ? <span className="text-xs opacity-80">(выбрано)</span> : null}
                        </div>
                      );
                    })}
                  </div>
                )}

                {(type === 'text' || type === 'fill') && !split && (
                  <div className="mt-3 space-y-2 text-sm">
                    <div>
                      <span className="text-slate-500 dark:text-slate-400">Ответ:</span> {userText || <i>—</i>}
                    </div>
                    {Array.isArray(q.acceptedAnswers) && q.acceptedAnswers.length > 0 && (
                      <div>
                        <span className="text-slate-500 dark:text-slate-400">Правильные ответы:</span>{' '}
                        {q.acceptedAnswers.join(', ')}
                      </div>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>
    );
  };

  const handleDeleteAll = async () => {
    if (!userId) return;
    const ok = window.confirm('Удалить все решения выбранного пользователя?');
    if (!ok) return;
    await deleteUserSolutions(userId);
    await loadSolutions();
  };

  const handleDeleteUser = async () => {
    if (!userId) return;
    const ok = window.confirm(
      'Удалить аккаунт выбранного пользователя вместе со всеми его решениями?'
    );
    if (!ok) return;

    try {
      await deleteUser(userId);
      setSolutions([]);
      setUsers((prev) => prev.filter((u) => u.id !== userId));
      setUserId('');
    } catch (e) {
      console.error('Failed to delete user', e);
    }
  };

  const selectedUser = users.find((u) => u.id === userId) || null;

  return (
    <Layout>
      <div className="container-app py-6 space-y-4">
        <h1 className="text-2xl font-semibold">Решения студентов</h1>

        <Card className="p-4 space-y-3">
          <div className="flex flex-wrap items-center gap-2">
            <Button variant={tab === 'code' ? 'primary' : 'outline'} onClick={() => setTab('code')}>
              Код
            </Button>
            <Button variant={tab === 'tests' ? 'primary' : 'outline'} onClick={() => setTab('tests')}>
              Тесты
            </Button>
          </div>

          <div className="flex flex-wrap gap-3 items-end">
            <div className="space-y-1">
              <div className="text-xs uppercase tracking-wide text-slate-500">
                Поиск пользователя
              </div>
              <div className="flex gap-2">
                <Input
                  placeholder="email / имя / фамилия"
                  value={q}
                  onChange={(e) => setQ(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') loadUsers();
                  }}
                />
                <Button onClick={loadUsers} disabled={searchLoading}>
                  Найти
                </Button>
              </div>
            </div>

            <div className="space-y-1 min-w-[220px]">
              <div className="text-xs uppercase tracking-wide text-slate-500">
                Пользователь
              </div>
              <Select value={userId} onChange={(e) => setUserId(e.target.value)}>
                <option value="">— не выбрано —</option>
                {users.map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.email} ({u.firstName} {u.lastName})
                  </option>
                ))}
              </Select>
            </div>

            <div className="space-y-1">
              <div className="text-xs uppercase tracking-wide text-slate-500">
                Период
              </div>
              <Select
                value={filterDays === null ? '' : String(filterDays)}
                onChange={(e) => {
                  const v = e.target.value;
                  setFilterDays(v === '' ? null : Number(v));
                }}
              >
                {FILTER_OPTIONS.map((opt) => (
                  <option key={opt.label} value={opt.value === null ? '' : opt.value}>
                    {opt.label}
                  </option>
                ))}
              </Select>
            </div>

            <div className="flex items-end gap-2">
              <Button
                onClick={() => (tab === 'tests' ? loadTestAttempts() : loadSolutions())}
                disabled={!userId || listLoading || testListLoading || searchLoading}
              >
                {tab === 'tests' ? 'Загрузить попытки тестов' : 'Загрузить решения'}
              </Button>
              <Button
                intent="danger"
                onClick={handleDeleteAll}
                disabled={!userId || listLoading}
              >
                Удалить все решения
              </Button>
              <Button
                intent="danger"
                onClick={handleDeleteUser}
                disabled={!userId || listLoading}
              >
                Удалить аккаунт
              </Button>
            </div>
          </div>

          {selectedUser && (
            <div className="text-xs text-slate-600 dark:text-slate-300">
              Выбран: <span className="font-mono">{selectedUser.email}</span>
            </div>
          )}
        </Card>

        {tab === 'code' && listLoading && (
          <div className="text-slate-600 dark:text-slate-300">Загрузка…</div>
        )}
        {tab === 'tests' && testListLoading && (
          <div className="text-slate-600 dark:text-slate-300">Загрузка…</div>
        )}

        {tab === 'code' && !listLoading && displayedSolutions.length > 0 && (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-slate-600 dark:text-slate-300">
              Всего решений по коду: {displayedSolutions.length}
            </div>

            <div className="space-y-6">
              {displayedSolutions.map((item) => {
                const full = detailsMap[item.id] || null;
                const showCode = expandedId === item.id && full;

                return (
                  <div
                    key={item.id}
                    className="border border-slate-200 dark:border-slate-800/40 rounded-xl p-4 bg-[rgb(var(--card))]"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 mb-2">
                      <div>
                        <div className="font-medium text-slate-900 dark:text-slate-50">
                          {item.courseTitle} • {item.assignmentTitle}
                        </div>
                        <div className="text-xs text-slate-600 dark:text-slate-400">
                          {new Date(item.submittedAt).toLocaleString()} • {item.language}
                        </div>
                      </div>
                      <div className="flex gap-2 items-center">
                        {item.passedAllTests ? (
                          <Badge intent="success">Все тесты пройдены ({item.passedCount})</Badge>
                        ) : (
                          <Badge intent="danger">
                            Провалено: {item.failedCount} / Пройдено: {item.passedCount}
                          </Badge>
                        )}
                        <Button size="sm" onClick={() => handleToggleCode(item.id)}>
                          {expandedId === item.id ? 'Скрыть код' : 'Показать код'}
                        </Button>
                      </div>
                    </div>

                    {showCode && (
                      <div className="mt-3 rounded-xl overflow-hidden border border-slate-700">
                        <CodeEditor
                          language={full.language || item.language}
                          value={full.submittedCode || ''}
                          readOnly
                          onChange={() => {}}
                          height={360}
                        />
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </Card>
        )}

        {tab === 'tests' && !testListLoading && displayedAttempts.length > 0 && (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-slate-600 dark:text-slate-300">
              Всего попыток тестов: {displayedAttempts.length}
            </div>

            <div className="space-y-6">
              {displayedAttempts.map((a) => {
                const id = a.attemptId;
                const dto = testDetailsMap[id] || null;
                const expanded = expandedTestAttemptId === id;

                return (
                  <div key={id} className="border border-slate-200 dark:border-slate-800/40 rounded-xl p-4 bg-[rgb(var(--card))]">
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                      <div>
                        <div className="font-medium text-slate-900 dark:text-slate-50">
                          {a.courseTitle} • {a.assignmentTitle}
                        </div>
                        <div className="text-xs text-slate-600 dark:text-slate-400">
                          {new Date(a.submittedAt).toLocaleString()} • попытка #{a.attemptNumber}
                        </div>
                      </div>
                      <div className="flex gap-2 items-center">
                        <Badge intent={a.passed ? 'success' : 'danger'}>{a.scorePercent}%</Badge>
                        {a.allowReview === false ? (
                          <Badge intent="secondary">Скрыт для студента</Badge>
                        ) : null}
                        <Button size="sm" onClick={() => handleToggleTestAttempt(a)}>
                          {expanded ? 'Скрыть' : 'Просмотреть'}
                        </Button>
                      </div>
                    </div>

                    {expanded ? renderAttemptReview(dto) : null}
                  </div>
                );
              })}
            </div>
          </Card>
        )}

        {tab === 'code' && !listLoading && !displayedSolutions.length && selectedUser && (
          <Card className="p-4 text-slate-600 dark:text-slate-400">
            Для этого пользователя нет решений по коду за выбранный период.
          </Card>
        )}

        {tab === 'tests' && !testListLoading && !displayedAttempts.length && selectedUser && (
          <Card className="p-4 text-slate-600 dark:text-slate-400">
            Для этого пользователя нет попыток тестов за выбранный период.
          </Card>
        )}
      </div>
    </Layout>
  );
}
