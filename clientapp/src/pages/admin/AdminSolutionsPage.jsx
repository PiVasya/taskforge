import React, { useCallback, useEffect, useRef, useState } from 'react';
import Layout from '../../components/Layout';
import { Card, Button, Input, Select, Badge } from '../../components/ui';
import { searchUsersOnce, getUserSolutions, getSolutionDetails } from '../../api/admin';
import CodeEditor from '../../components/CodeEditor';

const PAGE_SIZE = 5; // грузим по 5 карточек-решений

export default function AdminSolutionsPage() {
  // --------- поиск студентов ----------------
  const [q, setQ] = useState('');
  const [users, setUsers] = useState([]);
  const [userId, setUserId] = useState('');
  const [searchLoading, setSearchLoading] = useState(false);

  const handleSearchEnter = async (e) => {
    if (e.key !== 'Enter') return;
    setSearchLoading(true);
    try {
      const res = await searchUsersOnce(q.trim(), 20);
      setUsers(res);
      // авто-выбор первого найденного
      if (res?.length) setUserId(res[0].id);
    } finally {
      setSearchLoading(false);
    }
  };

  // --------- горизонтальная лента решений ---
  const [listSkip, setListSkip] = useState(0);        // сколько уже пропущено
  const [listTotal, setListTotal] = useState(null);   // можно хранить при необходимости
  const [columns, setColumns] = useState([]);         // карточки (детали с кодом)
  const [listLoading, setListLoading] = useState(false);

  const scrollerRef = useRef(null);

  const resetFeed = () => {
    setColumns([]);
    setListSkip(0);
  };

  // стартовая загрузка 5 решений с кодом
  const loadNextPage = useCallback(async () => {
    if (!userId || listLoading) return;
    setListLoading(true);
    try {
      // 1) получаем список ID без кода
      const list = await getUserSolutions(userId, { skip: listSkip, take: PAGE_SIZE });
      // 2) на каждый — отдельный запрос деталей (код + стат)
      const details = await Promise.all(list.map(x => getSolutionDetails(x.id)));
      setColumns(prev => [...prev, ...details.filter(Boolean)]);
      setListSkip(prev => prev + (list?.length || 0));
      if (listTotal == null && list.length < PAGE_SIZE) setListTotal(listSkip + list.length);
    } finally {
      setListLoading(false);
    }
  }, [userId, listSkip, listLoading, listTotal]);

  // кнопка "Загрузить решения" или авто после выбора студента
  const handleLoadInitial = async () => {
    resetFeed();
    await loadNextPage();
    // авто-скролл в начало
    if (scrollerRef.current) scrollerRef.current.scrollLeft = 0;
  };

  // при смене пользователя — сбрасываем и грузим стартовые 5
  useEffect(() => {
    if (!userId) return;
    (async () => {
      resetFeed();
      await loadNextPage();
      if (scrollerRef.current) scrollerRef.current.scrollLeft = 0;
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [userId]);

  // ленивый догруз справа
  const onScroll = (e) => {
    const el = e.currentTarget;
    if (!el) return;
    const nearRight = el.scrollLeft + el.clientWidth >= el.scrollWidth - 160; // порог
    if (nearRight && !listLoading) {
      loadNextPage();
    }
  };

  // ----------------- рендер -------------------
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
                onChange={e => setQ(e.target.value)}
                onKeyDown={handleSearchEnter}
                placeholder="email / имя / фамилия — Enter для поиска"
              />
            </div>
            <div>
              <label className="label">Студент</label>
              <Select value={userId} onChange={e => setUserId(e.target.value)}>
                <option value="">— выберите —</option>
                {users.map(u => (
                  <option key={u.id} value={u.id}>
                    {u.email} ({u.firstName} {u.lastName})
                  </option>
                ))}
              </Select>
            </div>
            <div className="flex items-end">
              <Button onClick={handleLoadInitial} disabled={!userId || listLoading || searchLoading}>
                Загрузить решения
              </Button>
            </div>
          </div>
          <div className="text-xs text-slate-500">
            {searchLoading ? 'Поиск…' : 'Введите запрос и нажмите Enter'}
          </div>
        </Card>

        {/* Горизонтальная лента карточек решений */}
        <Card className="p-4">
          <div
            ref={scrollerRef}
            onScroll={onScroll}
            className="overflow-x-auto whitespace-nowrap space-x-4 flex"
            style={{ scrollSnapType: 'x mandatory' }}
          >
            {columns.map(item => (
              <div
                key={item.id}
                className="w-[720px] min-w-[720px] snap-start rounded-2xl bg-slate-900/40 border border-slate-700 p-4 flex-shrink-0"
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
                  {item.passedAllTests
                    ? <Badge intent="success">Все тесты пройдены ({item.passedCount})</Badge>
                    : <Badge intent="danger">Провалено: {item.failedCount} / Пройдено: {item.passedCount}</Badge>}
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

            {!columns.length && !listLoading && (
              <div className="text-slate-400">Нет данных. Выберите студента и нажмите «Загрузить решения».</div>
            )}
          </div>

          <div className="pt-3 text-sm text-slate-400">
            {listLoading ? 'Загружаем ещё…' : 'Прокрутите вправо, чтобы подгрузить следующие решения'}
          </div>
        </Card>
      </div>
    </Layout>
  );
}
