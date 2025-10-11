import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Card, Button, Input, Select, Badge } from '../../components/ui';
import { searchUsers, getUserSolutions, getSolutionDetails } from '../../api/admin';
import CodeEditor from '../../components/CodeEditor';

export default function AdminSolutionsPage() {
  const [q, setQ] = useState('');
  const [users, setUsers] = useState([]);
  const [userId, setUserId] = useState(null);

  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(false);

  const [details, setDetails] = useState(null);

  useEffect(() => {
    let active = true;
    (async () => {
      const data = await searchUsers(q, 20);
      if (active) setUsers(data);
    })();
    return () => { active = false; };
  }, [q]);

  const loadSolutions = async () => {
    if (!userId) return;
    setLoading(true);
    try {
      const rows = await getUserSolutions(userId, { take: 200 });
      setItems(rows);
    } finally { setLoading(false); }
  };

  const openDetails = async (id) => {
    const d = await getSolutionDetails(id);
    setDetails(d);
  };

  return (
    <Layout>
      <div className="container-app py-6 space-y-4">
        <h1 className="text-2xl font-semibold">Решения студентов</h1>

        <Card className="p-4 space-y-3">
          <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
            <div>
              <label className="label">Поиск студента</label>
              <Input value={q} onChange={e=>setQ(e.target.value)} placeholder="email / имя / фамилия" />
            </div>
            <div>
              <label className="label">Студент</label>
              <Select value={userId ?? ''} onChange={e=>setUserId(e.target.value || null)}>
                <option value="">— выберите —</option>
                {users.map(u => (
                  <option key={u.id} value={u.id}>
                    {u.email} ({u.firstName} {u.lastName})
                  </option>
                ))}
              </Select>
            </div>
            <div className="flex items-end">
              <Button onClick={loadSolutions} disabled={!userId || loading}>
                Загрузить решения
              </Button>
            </div>
          </div>
        </Card>

        <Card className="p-0 overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead className="bg-slate-50 dark:bg-slate-800/50">
            <tr>
              <th className="th">Дата</th>
              <th className="th">Курс</th>
              <th className="th">Задание</th>
              <th className="th">Язык</th>
              <th className="th">Результат</th>
              <th className="th"></th>
            </tr>
            </thead>
            <tbody>
            {items.map(row => (
              <tr key={row.id} className="border-t">
                <td className="td">{new Date(row.submittedAt).toLocaleString()}</td>
                <td className="td">{row.courseTitle}</td>
                <td className="td">{row.assignmentTitle}</td>
                <td className="td">{row.language}</td>
                <td className="td">
                  {row.passedAllTests
                    ? <Badge intent="success">Passed ({row.passedCount})</Badge>
                    : <Badge intent="danger">Failed ({row.failedCount})</Badge>}
                </td>
                <td className="td text-right">
                  <Button size="sm" onClick={()=>openDetails(row.id)}>Посмотреть</Button>
                </td>
              </tr>
            ))}
            {!items.length && (
              <tr><td className="td" colSpan={6}>Нет данных</td></tr>
            )}
            </tbody>
          </table>
        </Card>

        {details && (
          <Card className="p-4 space-y-3">
            <div className="flex items-center justify-between">
              <div className="font-medium">{details.courseTitle} — {details.assignmentTitle}</div>
              <Button size="sm" onClick={()=>setDetails(null)}>Закрыть</Button>
            </div>
            <div className="text-xs text-slate-500">
              {new Date(details.submittedAt).toLocaleString()} • {details.language} •
              {details.passedAllTests ? ' Все тесты пройдены' : ` Пройдено: ${details.passedCount}, не пройдено: ${details.failedCount}`}
            </div>
            <CodeEditor
              language={details.language}
              value={details.submittedCode}
              onChange={()=>{}}
              readOnly
              height={420}
            />
          </Card>
        )}
      </div>
    </Layout>
  );
}
