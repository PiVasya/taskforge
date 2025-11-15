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
import CodeEditor from '../../components/CodeEditor';

const FILTER_OPTIONS = [
  { label: 'За всё время', value: null },
  { label: 'За сегодня', value: 1 },
  { label: 'За неделю', value: 7 },
  { label: 'За месяц', value: 30 },
];

export default function AdminSolutionsPage() {
  const [q, setQ] = useState('');
  const [users, setUsers] = useState([]);
  const [userId, setUserId] = useState('');
  const [searchLoading, setSearchLoading] = useState(false);

  const [solutions, setSolutions] = useState([]);
  const [listLoading, setListLoading] = useState(false);
  const [filterDays, setFilterDays] = useState(null);

  const [detailsMap, setDetailsMap] = useState({});
  const [expandedId, setExpandedId] = useState(null);

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

  useEffect(() => {
    if (userId) {
      loadSolutions();
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

  const handleDeleteAll = async () => {
    if (!userId) return;
    const ok = window.confirm('Удалить все решения выбранного пользователя?');
    if (!ok) return;
    await deleteUserSolutions(userId);
    await loadSolutions();
  };

  const handleDeleteUser = async () => {
    if (!userId) return;
    const ok = window.confirm('Удалить аккаунт выбранного пользователя вместе со всеми его решениями?');
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
          <div className="flex flex-wrap gap-3 items-end">
            <div className="space-y-1">
              <div className="text-xs uppercase tracking-wide text-slate-500">Поиск пользователя</div>
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
              <div className="text-xs uppercase tracking-wide text-slate-500">Пользователь</div>
              <Select
                value={userId}
                onChange={(e) => setUserId(e.target.value)}
              >
                <option value="">— не выбрано —</option>
                {users.map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.email} ({u.firstName} {u.lastName})
                  </option>
                ))}
              </Select>
            </div>

            <div className="space-y-1">
              <div className="text-xs uppercase tracking-wide text-slate-500">Период</div>
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
              <Button onClick={loadSolutions} disabled={!userId || listLoading || searchLoading}>
                Загрузить решения
              </Button>
              <Button intent="danger" onClick={handleDeleteAll} disabled={!userId || listLoading}>
                Удалить все решения
              </Button>
              <Button intent="danger" onClick={handleDeleteUser} disabled={!userId || listLoading}>
                Удалить аккаунт
              </Button>
            </div>
          </div>

          {selectedUser && (
            <div className="text-xs text-slate-400">
              Выбран: <span className="font-mono">{selectedUser.email}</span>
            </div>
          )}
        </Card>

        {listLoading && <div className="text-slate-400">Загрузка…</div>}

        {!listLoading && displayedSolutions.length > 0 && (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-slate-400">
              Всего решений: {displayedSolutions.length}
            </div>

            <div className="space-y-6">
              {displayedSolutions.map((item) => {
                const full = detailsMap[item.id] || null;
                const showCode = expandedId === item.id && full;

                return (
                  <div
                    key={item.id}
                    className="border border-slate-800/40 rounded-xl p-4 bg-slate-900/40"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 mb-2">
                      <div>
                        <div className="font-medium">
                          {item.courseTitle} • {item.assignmentTitle}
                        </div>
                        <div className="text-xs text-slate-400">
                          {new Date(item.submittedAt).toLocaleString()} • {item.language}
                        </div>
                      </div>
                      <div className="flex gap-2 items-center">
                        {item.passedAllTests ? (
                          <Badge intent="success">
                            Все тесты пройдены ({item.passedCount})
                          </Badge>
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

        {!listLoading && !displayedSolutions.length && selectedUser && (
          <Card className="p-4 text-slate-400">Для этого пользователя нет решений за выбранный период.</Card>
        )}
      </div>
    </Layout>
  );
}
