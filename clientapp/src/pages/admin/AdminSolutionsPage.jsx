import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Card, Button, Input, Select, Badge } from '../../components/ui';
import { Trash2, Users, UserPlus, X } from 'lucide-react';
import {
  searchUsersOnce,
  getUserSolutions,
  getSolutionDetails,
  getSolutionsDetailsBulkOrFallback,
  deleteUserSolutions,
  deleteUser,
  deleteSolution,
  getAdminUserGroupIds,
  getUserImageSolutions,
  getAdminImageSolutionDetails,
  deleteAdminImageSolution,
} from '../../api/admin';
import { getUserTaskTestAttempts, getAdminTaskTestAttemptReview } from '../../api/taskTestAttempts';
import { deleteAdminTaskTestAttempt } from '../../api/taskTestAttempts';
import { getAdminGroups, addGroupMember, removeGroupMember } from '../../api/groups';
import CodeEditor from '../../components/CodeEditor';

const FILTER_OPTIONS = [
  { label: 'За всё время', value: null },
  { label: 'За сегодня', value: 1 },
  { label: 'За неделю', value: 7 },
  { label: 'За месяц', value: 30 },
  { label: 'Как можно больше', value: 1000 },
];

export default function AdminSolutionsPage() {
  const [tab, setTab] = useState('code'); // 'code' | 'tests' | 'images' | 'groups'

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

  const [imageSolutions, setImageSolutions] = useState([]);
  const [imageListLoading, setImageListLoading] = useState(false);
  const [imageDetailsMap, setImageDetailsMap] = useState({});
  const [expandedImageId, setExpandedImageId] = useState(null);

  const [groups, setGroups] = useState([]);
  const [groupsLoading, setGroupsLoading] = useState(false);
  const [userGroupIds, setUserGroupIds] = useState([]);
  const [groupToAdd, setGroupToAdd] = useState('');

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

  const loadImageSolutions = async () => {
    if (!userId) {
      setImageSolutions([]);
      return;
    }
    setImageListLoading(true);
    try {
      const data = await getUserImageSolutions(userId, { days: filterDays });
      setImageSolutions(Array.isArray(data) ? data : []);
      setExpandedImageId(null);
      setImageDetailsMap({});
    } catch (e) {
      console.error('Failed to load image solutions', e);
    } finally {
      setImageListLoading(false);
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

  const loadGroups = async () => {
    setGroupsLoading(true);
    try {
      const list = await getAdminGroups();
      setGroups(Array.isArray(list) ? list : []);
    } catch (e) {
      console.error('Failed to load groups', e);
    } finally {
      setGroupsLoading(false);
    }
  };

  const loadUserGroups = async () => {
    if (!userId) {
      setUserGroupIds([]);
      return;
    }
    try {
      const ids = await getAdminUserGroupIds(userId);
      setUserGroupIds(Array.isArray(ids) ? ids : []);
    } catch (e) {
      console.error('Failed to load user groups', e);
      setUserGroupIds([]);
    }
  };

  useEffect(() => {
    if (userId) {
      loadSolutions();
      loadTestAttempts();
      loadImageSolutions();
      loadUserGroups();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterDays, userId]);

  useEffect(() => {
    // список групп нужен только админке, подгружаем один раз
    loadGroups();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

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

  const displayedImageSolutions = useMemo(() => {
    const list = [...(imageSolutions || [])];
    list.sort((a, b) => new Date(b.createdAtUtc) - new Date(a.createdAtUtc));
    return list;
  }, [imageSolutions]);

  const userGroupSet = useMemo(() => new Set(userGroupIds || []), [userGroupIds]);

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


  const handleToggleImageSolution = async (id) => {
    if (expandedImageId === id) {
      setExpandedImageId(null);
      return;
    }

    if (!imageDetailsMap[id]) {
      try {
        const dto = await getAdminImageSolutionDetails(id);
        setImageDetailsMap((prev) => ({ ...prev, [id]: dto }));
      } catch (e) {
        console.error('Failed to load image solution details', e);
        return;
      }
    }

    setExpandedImageId(id);
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
              <div key={q.id || i} className="rounded-xl border border-neutral-200 dark:border-neutral-700 p-4 bg-[rgb(var(--card))]">
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
                      <span className="text-neutral-500 dark:text-neutral-400">Ответ:</span> {userText || <i>—</i>}
                    </div>
                    {Array.isArray(q.acceptedAnswers) && q.acceptedAnswers.length > 0 && (
                      <div>
                        <span className="text-neutral-500 dark:text-neutral-400">Правильные ответы:</span>{' '}
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

  const handleDeleteSolution = async (id) => {
    const ok = window.confirm('Удалить это решение (код)?');
    if (!ok) return;
    try {
      await deleteSolution(id);
      setSolutions((prev) => prev.filter((x) => x.id !== id));
      setDetailsMap((prev) => {
        const copy = { ...prev };
        delete copy[id];
        return copy;
      });
      if (expandedId === id) setExpandedId(null);
    } catch (e) {
      console.error('Failed to delete solution', e);
    }
  };

  const handleDeleteAttempt = async (attemptId) => {
    const ok = window.confirm('Удалить эту попытку теста?');
    if (!ok) return;
    try {
      await deleteAdminTaskTestAttempt(attemptId);
      setTestAttempts((prev) => prev.filter((x) => x.attemptId !== attemptId));
      setTestDetailsMap((prev) => {
        const copy = { ...prev };
        delete copy[attemptId];
        return copy;
      });
      if (expandedTestAttemptId === attemptId) setExpandedTestAttemptId(null);
    } catch (e) {
      console.error('Failed to delete test attempt', e);
    }
  };

  const handleAddToGroup = async () => {
    if (!userId || !groupToAdd) return;
    try {
      await addGroupMember(groupToAdd, userId);
      setGroupToAdd('');
      await loadUserGroups();
    } catch (e) {
      console.error('Failed to add user to group', e);
    }
  };

  const handleRemoveFromGroup = async (groupId) => {
    if (!userId || !groupId) return;
    try {
      await removeGroupMember(groupId, userId);
      await loadUserGroups();
    } catch (e) {
      console.error('Failed to remove user from group', e);
    }
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
        <h1 className="text-2xl font-semibold">Управление пользователями</h1>

        <Card className="p-4 space-y-3">
          <div className="flex flex-wrap items-center gap-2">
            <Button variant={tab === 'code' ? 'primary' : 'outline'} onClick={() => setTab('code')}>
              Код
            </Button>
            <Button variant={tab === 'tests' ? 'primary' : 'outline'} onClick={() => setTab('tests')}>
              Тесты
            </Button>
            <Button variant={tab === 'images' ? 'primary' : 'outline'} onClick={() => setTab('images')}>
              Картинки
            </Button>
            <Button variant={tab === 'groups' ? 'primary' : 'outline'} onClick={() => setTab('groups')}>
              Группы
            </Button>
          </div>

          <div className="flex flex-wrap gap-3 items-end">
            <div className="space-y-1">
              <div className="text-xs uppercase tracking-wide text-neutral-500">
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
              <div className="text-xs uppercase tracking-wide text-neutral-500">
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
              <div className="text-xs uppercase tracking-wide text-neutral-500">
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
                onClick={() => {
                  if (tab === 'tests') return loadTestAttempts();
                  if (tab === 'images') return loadImageSolutions();
                  if (tab === 'groups') return loadUserGroups();
                  return loadSolutions();
                }}
                disabled={!userId || listLoading || testListLoading || searchLoading}
              >
                {tab === 'tests'
                  ? 'Загрузить попытки тестов'
                  : tab === 'images'
                    ? 'Загрузить решения (картинки)'
                    : tab === 'groups'
                      ? 'Обновить группы'
                      : 'Загрузить решения'}
              </Button>
              {tab === 'code' && (
                <Button intent="danger" onClick={handleDeleteAll} disabled={!userId || listLoading}>
                  Удалить все решения
                </Button>
              )}
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
            <div className="text-xs text-neutral-600 dark:text-neutral-300">
              Выбран: <span className="font-mono">{selectedUser.email}</span>
            </div>
          )}
        </Card>

        {tab === 'code' && listLoading && (
          <div className="text-neutral-600 dark:text-neutral-300">Загрузка…</div>
        )}
        {tab === 'tests' && testListLoading && (
          <div className="text-neutral-600 dark:text-neutral-300">Загрузка…</div>
        )}
        {tab === 'images' && imageListLoading && (
          <div className="text-neutral-600 dark:text-neutral-300">Загрузка…</div>
        )}

        {tab === 'code' && !listLoading && displayedSolutions.length > 0 && (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-neutral-600 dark:text-neutral-300">
              Всего решений по коду: {displayedSolutions.length}
            </div>

            <div className="space-y-6">
              {displayedSolutions.map((item) => {
                const full = detailsMap[item.id] || null;
                const showCode = expandedId === item.id && full;

                return (
                  <div
                    key={item.id}
                    className="border border-neutral-200 dark:border-neutral-800/40 rounded-xl p-4 bg-[rgb(var(--card))]"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 mb-2">
                      <div>
                        <div className="font-medium text-neutral-900 dark:text-neutral-50">
                          {item.courseTitle} • {item.assignmentTitle}
                        </div>
                        <div className="text-xs text-neutral-600 dark:text-neutral-400">
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
                        <Button
                          variant="outline"
                          className="inline-flex items-center gap-2"
                          onClick={() => handleToggleCode(item.id)}
                        >
                          {expandedId === item.id ? 'Скрыть код' : 'Показать код'}
                        </Button>
                        <Button
                          variant="outline"
                          intent="danger"
                          className="inline-flex items-center gap-2"
                          onClick={() => handleDeleteSolution(item.id)}
                          title="Удалить это решение"
                        >
                          <Trash2 size={16} />
                        </Button>
                      </div>
                    </div>

                    {showCode && (
                      <div className="mt-3 rounded-xl overflow-hidden border border-neutral-700">
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

        
        {tab === 'images' && !imageListLoading && displayedImageSolutions.length > 0 && (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-neutral-600 dark:text-neutral-300">
              Всего решений по картинкам: {displayedImageSolutions.length}
            </div>

            <div className="space-y-6">
              {displayedImageSolutions.map((item) => {
                const full = imageDetailsMap[item.id] || null;
                const expanded = expandedImageId === item.id && full;

                return (
                  <div
                    key={item.id}
                    className="border border-neutral-200 dark:border-neutral-800/40 rounded-xl p-4 bg-[rgb(var(--card))]"
                  >
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <div className="space-y-1">
                        <div className="font-medium">
                          {item.assignmentTitle}{' '}
                          <span className="text-sm text-neutral-500">({item.kind}{item.isTrial ? ', пробник' : ''})</span>
                        </div>
                        <div className="text-xs text-neutral-500">
                          {new Date(item.createdAtUtc).toLocaleString()} • {item.language || '—'}
                        </div>
                      </div>

                      <div className="flex items-center gap-2">
                        {item.passed === true ? (
                          <Badge intent="success">Зачёт</Badge>
                        ) : item.passed === false ? (
                          <Badge intent="danger">Не зачтено</Badge>
                        ) : (
                          <Badge intent="secondary">Без сравнения</Badge>
                        )}

                        {typeof item.similarityPercent === 'number' ? (
                          <Badge intent="secondary">
                            {Math.round(item.similarityPercent)}% (порог {Math.round(item.thresholdPercent || 0)}%)
                          </Badge>
                        ) : null}

                        <Button variant="outline" onClick={() => handleToggleImageSolution(item.id)}>
                          {expanded ? 'Скрыть' : 'Открыть'}
                        </Button>

                        <Button
                          intent="danger"
                          variant="outline"
                          onClick={async () => {
                            const ok = window.confirm('Удалить это решение (картинки)?');
                            if (!ok) return;
                            try {
                              await deleteAdminImageSolution(item.id);
                              setImageSolutions((prev) => prev.filter((x) => x.id !== item.id));
                              setImageDetailsMap((prev) => {
                                const copy = { ...prev };
                                delete copy[item.id];
                                return copy;
                              });
                              if (expandedImageId === item.id) setExpandedImageId(null);
                            } catch (e) {
                              console.error('Failed to delete image solution', e);
                            }
                          }}
                        >
                          <Trash2 className="w-4 h-4" />
                        </Button>
                      </div>
                    </div>

                    {expanded ? (
                      <div className="mt-4 space-y-4">
                        {full.runnerError ? (
                          <div className="text-sm text-red-600 whitespace-pre-wrap">{full.runnerError}</div>
                        ) : null}

                        <div className="grid gap-4 md:grid-cols-2">
                          <div className="space-y-2">
                            <div className="text-xs uppercase tracking-wide text-neutral-500">Эталон</div>
                            {full.referenceUrl ? (
                              <img src={full.referenceUrl} alt="reference" className="w-full rounded-lg border border-neutral-200 dark:border-neutral-700" />
                            ) : (
                              <div className="text-sm text-neutral-500">—</div>
                            )}
                          </div>

                          <div className="space-y-2">
                            <div className="text-xs uppercase tracking-wide text-neutral-500">Результат</div>
                            {full.submittedUrl ? (
                              <img src={full.submittedUrl} alt="submitted" className="w-full rounded-lg border border-neutral-200 dark:border-neutral-700" />
                            ) : (
                              <div className="text-sm text-neutral-500">—</div>
                            )}
                          </div>
                        </div>

                        {full.submittedCode ? (
                          <div className="space-y-2">
                            <div className="text-xs uppercase tracking-wide text-neutral-500">Код</div>
                            <CodeEditor value={full.submittedCode} language={full.language || 'python'} readOnly />
                          </div>
                        ) : null}

                        {(full.stdout || full.stderr) ? (
                          <div className="grid gap-4 md:grid-cols-2">
                            <div>
                              <div className="text-xs uppercase tracking-wide text-neutral-500 mb-1">stdout</div>
                              <pre className="text-xs whitespace-pre-wrap rounded-lg border border-neutral-200 dark:border-neutral-700 p-3">
                                {full.stdout || ''}
                              </pre>
                            </div>
                            <div>
                              <div className="text-xs uppercase tracking-wide text-neutral-500 mb-1">stderr</div>
                              <pre className="text-xs whitespace-pre-wrap rounded-lg border border-neutral-200 dark:border-neutral-700 p-3">
                                {full.stderr || ''}
                              </pre>
                            </div>
                          </div>
                        ) : null}
                      </div>
                    ) : null}
                  </div>
                );
              })}
            </div>
          </Card>
        )}

{tab === 'tests' && !testListLoading && displayedAttempts.length > 0 && (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-neutral-600 dark:text-neutral-300">
              Всего попыток тестов: {displayedAttempts.length}
            </div>

            <div className="space-y-6">
              {displayedAttempts.map((a) => {
                const id = a.attemptId;
                const dto = testDetailsMap[id] || null;
                const expanded = expandedTestAttemptId === id;

                return (
                  <div key={id} className="border border-neutral-200 dark:border-neutral-800/40 rounded-xl p-4 bg-[rgb(var(--card))]">
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                      <div>
                        <div className="font-medium text-neutral-900 dark:text-neutral-50">
                          {a.courseTitle} • {a.assignmentTitle}
                        </div>
                        <div className="text-xs text-neutral-600 dark:text-neutral-400">
                          {new Date(a.submittedAt).toLocaleString()} • попытка #{a.attemptNumber}
                        </div>
                      </div>
                      <div className="flex gap-2 items-center">
                        <Badge intent={a.passed ? 'success' : 'danger'}>{a.scorePercent}%</Badge>
                        {a.allowReview === false ? (
                          <Badge intent="secondary">Скрыт для студента</Badge>
                        ) : null}
                        <Button
                          variant="outline"
                          className="inline-flex items-center gap-2"
                          onClick={() => handleToggleTestAttempt(a)}
                        >
                          {expanded ? 'Скрыть' : 'Просмотреть'}
                        </Button>
                        <Button
                          variant="outline"
                          intent="danger"
                          className="inline-flex items-center gap-2"
                          onClick={() => handleDeleteAttempt(id)}
                          title="Удалить эту попытку"
                        >
                          <Trash2 size={16} />
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
          <Card className="p-4 text-neutral-600 dark:text-neutral-400">
            Для этого пользователя нет решений по коду за выбранный период.
          </Card>
        )}

        {tab === 'tests' && !testListLoading && !displayedAttempts.length && selectedUser && (
          <Card className="p-4 text-neutral-600 dark:text-neutral-400">
            Для этого пользователя нет попыток тестов за выбранный период.
          </Card>
        )}

        {tab === 'groups' && selectedUser && (
          <Card className="p-4 space-y-4">
            <div className="flex items-center gap-2 text-sm text-neutral-600 dark:text-neutral-300">
              <Users size={18} />
              <span>Группы пользователя</span>
            </div>

            <div className="flex flex-wrap items-end gap-2">
              <div className="space-y-1 min-w-[260px]">
                <div className="text-xs uppercase tracking-wide text-neutral-500">Добавить в группу</div>
                <Select value={groupToAdd} onChange={(e) => setGroupToAdd(e.target.value)} disabled={groupsLoading}>
                  <option value="">— выберите группу —</option>
                  {groups
                    .filter((g) => !userGroupSet.has(g.id))
                    .map((g) => (
                      <option key={g.id} value={g.id}>
                        {g.name} ({g.code})
                      </option>
                    ))}
                </Select>
              </div>
              <Button
                variant="outline"
                className="inline-flex items-center gap-2"
                onClick={handleAddToGroup}
                disabled={!groupToAdd || !userId}
              >
                <UserPlus size={16} /> Добавить
              </Button>
            </div>

            <div className="space-y-2">
              {userGroupIds.length === 0 ? (
                <div className="text-neutral-600 dark:text-neutral-400">Пользователь не состоит ни в одной группе.</div>
              ) : (
                <div className="flex flex-wrap gap-2">
                  {groups
                    .filter((g) => userGroupSet.has(g.id))
                    .map((g) => (
                      <div
                        key={g.id}
                        className="inline-flex items-center gap-2 rounded-full border border-neutral-200 dark:border-neutral-700 px-3 py-1 text-sm"
                      >
                        <span className="font-medium">{g.name}</span>
                        <span className="text-xs opacity-70">({g.code})</span>
                        <button
                          className="opacity-70 hover:opacity-100"
                          title="Убрать из группы"
                          onClick={() => handleRemoveFromGroup(g.id)}
                        >
                          <X size={14} />
                        </button>
                      </div>
                    ))}
                </div>
              )}
            </div>
          </Card>
        )}
      </div>
    </Layout>
  );
}
