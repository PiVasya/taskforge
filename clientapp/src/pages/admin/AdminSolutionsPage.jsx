import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Card, Button, Input, Select, Badge } from '../../components/ui';
import {
  searchUsersOnce,
  getUserSolutions,
  getSolutionsDetailsBulkOrFallback,
  deleteAllUserSolutions,
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

  const handleSearchEnter = async (e) => {
    if (e.key !== 'Enter') return;
    setSearchLoading(true);
    try {
      const res = await searchUsersOnce(q.trim(), 20);
      setUsers(res || []);
      if (res?.length) setUserId(res[0].id);
    } catch (err) {
      console.error('Failed to search users', err);
      alert('Не удалось выполнить поиск пользователей');
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
      // берём умеренную порцию, чтобы не долбить по 1000 сразу
      const take = 150;
      const baseList = await getUserSolutions(userId, { skip: 0, take });
      const list = Array.isArray(baseList) ? baseList : (baseList?.items ?? []);
      if (!list.length) {
        setSolutions([]);
        return;
      }

      const ids = list.map((x) => x.id);
      const details = await getSolutionsDetailsBulkOrFallback(ids, { concurrency: 8 });
      setSolutions(details || []);
    } catch (e) {
      console.error('loadSolutions error:', e);
      alert('Не удалось загрузить решения');
      setSolutions([]);
    } finally {
      setListLoading(false);
    }
  };

  useEffect(() => {
    if (!userId) return;
    loadSolutions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [userId]);

  const displayedSolutions = useMemo(() => {
    if (!filterDays) return solutions;
    const since = new Date();
    since.setDate(since.getDate() - filterDays);
    return (solutions || []).filter((s) => new Date(s.submittedAt) >= since);
  }, [solutions, filterDays]);

  const handleDeleteAll = async () => {
    if (!userId) return;
    if (!window.confirm('Вы уверены, что хотите удалить все решения выбранного пользователя?')) return;
    try {
      await deleteAllUserSolutions(userId);
      await loadSolutions();
    } catch (err) {
      console.error(err);
      alert('Не удалось удалить решения');
    }
  };

  return (
    <Layout>
      <div className="container-app py-6 space-y-4">
        <h1 className="text-2xl font-semibold">Решения студентов</h1>

        <Card className="p-4 space-y-3">
          <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
            <div>
              <label className="label">Поиск студента</label>
              <Input
                value={q}
                onChange={(e) => setQ(e.target.value)}
                onKeyDown={handleSearchEnter}
                placeholder="email / имя / фамилия — Enter для поиска"
              />
            </div>
            <div>
              <label className="label">Студент</label>
              <Select value={userId} onChange={(e) => setUserId(e.target.value)}>
                <option value="">— выберите —</option>
                {users.map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.email} ({u.firstName} {u.lastName})
                  </option>
                ))}
              </Select>
            </div>
            <div className="flex items-end space-x-2">
              <Button onClick={loadSolutions} disabled={!userId || listLoading || searchLoading}>
                Загрузить решения
              </Button>
              <Button intent="danger" onClick={handleDeleteAll} disabled={!userId || listLoading}>
                Удалить все решения
              </Button>
            </div>
          </div>
          <div className="text-xs text-slate-500">
            {searchLoading ? 'Поиск…' : 'Введите запрос и нажмите Enter'}
          </div>
        </Card>

        <Card className="p-4 space-y-4">
          <div className="flex flex-wrap gap-2 items-center">
            <span className="label">Период:</span>
            {FILTER_OPTIONS.map((opt) => (
              <Button
                key={opt.label}
                intent={filterDays === opt.value ? 'primary' : 'secondary'}
                onClick={() => setFilterDays(opt.value)}
              >
                {opt.label}
              </Button>
            ))}
          </div>

          {listLoading && <div className="text-slate-400">Загрузка…</div>}
          {!listLoading && !displayedSolutions.length && (
            <div className="text-slate-400">
              Нет данных. Выберите студента и нажмите «Загрузить решения».
            </div>
          )}

          <div className="space-y-4">
            {displayedSolutions.map((item) => (
              <div
                key={item.id}
                className="rounded-2xl bg-slate-900/40 border border-slate-700 p-4"
              >
                <div className="flex items-center justify-between mb-2">
                  <div className="font-medium">
                    {item.courseTitle} — {item.assignmentTitle}
                  </div>
                  <div className="text-xs text-slate-400">
                    {new Date(item.submittedAt).toLocaleString()} • {item.language}
                  </div>
                </div>
                <div className="mb-3">
                  {item.passedAllTests ? (
                    <Badge intent="success">Все тесты пройдены ({item.passedCount})</Badge>
                  ) : (
                    <Badge intent="danger">
                      Провалено: {item.failedCount} / Пройдено: {item.passedCount}
                    </Badge>
                  )}
                </div>
                <div className="rounded-xl overflow-hidden border border-slate-700">
                  <CodeEditor
                    language={item.language}
                    value={item.submittedCode || ''}
                    readOnly
                    onChange={() => {}}
                    height={360}
                  />
                </div>
              </div>
            ))}
          </div>
        </Card>
      </div>
    </Layout>
  );
}
