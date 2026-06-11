import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Card, Button, Input, Select, Badge } from '../../components/ui';
import { Trash2, Users, UserPlus, X } from 'lucide-react';
import {
  searchUsersOnce,
  getUserSolutions,
  getSolutionDetails,
  deleteUserSolutions,
  deleteSolution,
  getAdminUserGroupIds,
  getUserImageSolutions,
  getAdminImageSolutionDetails,
  deleteAdminImageSolution,
} from '../../api/admin';
import { getUserTaskTestAttempts, getAdminTaskTestAttemptReview } from '../../api/taskTestAttempts';
import { deleteAdminTaskTestAttempt } from '../../api/taskTestAttempts';
import { getAdminGroups, addGroupMember, removeGroupMember } from '../../api/groups';
import { getUserMathAttempts, getAdminMathAttemptReview, deleteAdminMathAttempt } from '../../api/mathTaskAttempts';
import MathAttemptReview from '../../components/math/MathAttemptReview';
import CodeEditor from '../../components/CodeEditor';
import { useNotify } from '../../components/notify/NotifyProvider';
import AppErrorPanel from '../../components/AppErrorPanel';
import { handleApiError } from '../../utils/handleApiError';
import {
  formatDateTime,
  getImageSolutionCode,
  getImageSolutionDate,
  getImageSolutionPercent,
  getImageSolutionThreshold,
  getImageSolutionTitle,
  getImageSolutionUrl,
  getRunnerText,
  getSolutionBadgeIntent,
  getSolutionCases,
  isResultCasePassed as isResultCasePassedStrict,
  getSolutionCode,
  getSolutionDate,
  getSolutionPassedFailed,
  getSolutionScore,
  getSolutionStatusLabel,
  getSolutionSubmittedAt,
  getSolutionTitle,
} from '../../utils/solutionDto';

const FILTER_OPTIONS = [
  { label: 'За всё время', value: null },
  { label: 'За сегодня', value: 1 },
  { label: 'За неделю', value: 7 },
  { label: 'За месяц', value: 30 },
  { label: 'Как можно больше', value: 1000 },
];

function CompactEmpty({ children }) {
  return (
    <div className="rounded-xl border border-dashed border-neutral-300 dark:border-neutral-700 px-4 py-3 text-sm text-neutral-500 dark:text-neutral-400">
      {children}
    </div>
  );
}

function RunnerOutput({ item }) {
  const stdout = getRunnerText(item, 'stdout');
  const stderr = getRunnerText(item, 'stderr');
  const runnerError = getRunnerText(item, 'runnerError') || getRunnerText(item, 'error');
  const cases = getSolutionCases(item);

  if (!stdout && !stderr && !runnerError && !cases.length) return null;

  return (
    <Card className="p-3 space-y-3">
      {runnerError ? <div className="text-sm text-red-600 whitespace-pre-wrap">{runnerError}</div> : null}
      {stdout ? (
        <div>
          <div className="text-xs uppercase tracking-wide text-neutral-500">stdout</div>
          <pre className="text-xs whitespace-pre-wrap rounded-lg border border-neutral-200 dark:border-neutral-700 p-3 mt-1">{stdout}</pre>
        </div>
      ) : null}
      {stderr ? (
        <div>
          <div className="text-xs uppercase tracking-wide text-neutral-500">stderr</div>
          <pre className="text-xs whitespace-pre-wrap rounded-lg border border-neutral-200 dark:border-neutral-700 p-3 mt-1">{stderr}</pre>
        </div>
      ) : null}
      {cases.length ? (
        <div className="space-y-2">
          <div className="text-xs uppercase tracking-wide text-neutral-500">Тесты</div>
          {cases.slice(0, 8).map((c, i) => {
            const ok = isResultCasePassedStrict(c);
            return (
              <div key={i} className="rounded-lg border border-neutral-200 dark:border-neutral-700 p-2 text-xs">
                <div className="flex items-center justify-between gap-2">
                  <span>Тест #{i + 1}</span>
                  <Badge intent={ok ? 'success' : 'danger'}>{ok ? 'OK' : 'FAIL'}</Badge>
                </div>
                {(c?.stderr || c?.compileStderr || c?.error) ? (
                  <pre className="mt-2 whitespace-pre-wrap break-words text-red-600">{c.stderr || c.compileStderr || c.error}</pre>
                ) : null}
              </div>
            );
          })}
          {cases.length > 8 ? <div className="text-xs text-neutral-500">… и ещё {cases.length - 8}</div> : null}
        </div>
      ) : null}
    </Card>
  );
}

export default function AdminSolutionsPage() {
  const notify = useNotify();
  const [pageError, setPageError] = useState(null);
  const [tab, setTab] = useState('code'); 

  const [q, setQ] = useState('');
  const [users, setUsers] = useState([]);
  const [userId, setUserId] = useState('');
  const [searchLoading, setSearchLoading] = useState(false);

  const [solutions, setSolutions] = useState([]);
  const [listLoading, setListLoading] = useState(false);
  const [filterDays, setFilterDays] = useState(null);

  const [detailsMap, setDetailsMap] = useState({});
  const [detailsLoadingMap, setDetailsLoadingMap] = useState({});
  const [expandedId, setExpandedId] = useState(null);

  const [testAttempts, setTestAttempts] = useState([]);
  const [testListLoading, setTestListLoading] = useState(false);
  const [testDetailsMap, setTestDetailsMap] = useState({});
  const [expandedTestAttemptId, setExpandedTestAttemptId] = useState(null);

  const [imageSolutions, setImageSolutions] = useState([]);
  const [imageListLoading, setImageListLoading] = useState(false);
  const [imageDetailsMap, setImageDetailsMap] = useState({});
  const [imageDetailsLoadingMap, setImageDetailsLoadingMap] = useState({});
  const [expandedImageId, setExpandedImageId] = useState(null);

  const [mathAttempts, setMathAttempts] = useState([]);
  const [mathListLoading, setMathListLoading] = useState(false);
  const [mathDetailsMap, setMathDetailsMap] = useState({});
  const [expandedMathAttemptId, setExpandedMathAttemptId] = useState(null);

  const [groups, setGroups] = useState([]);
  const [groupsLoading, setGroupsLoading] = useState(false);
  const [userGroupIds, setUserGroupIds] = useState([]);
  const [groupToAdd, setGroupToAdd] = useState('');

  const loadUsers = async () => {
    setSearchLoading(true);
    try {
      const data = await searchUsersOnce(q, 20);
      setUsers(data || []);
      setPageError(null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось найти пользователей');
      setPageError(parsed);
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
      setPageError(null);
      setExpandedId(null);
      setDetailsMap({});
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить решения');
      setPageError(parsed);
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
      setPageError(null);
      setExpandedImageId(null);
      setImageDetailsMap({});
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить image-решения');
      setPageError(parsed);
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
      setPageError(null);
      setExpandedTestAttemptId(null);
      setTestDetailsMap({});
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить попытки тестов');
      setPageError(parsed);
    } finally {
      setTestListLoading(false);
    }
  };

  const loadMathAttempts = async () => {
    if (!userId) {
      setMathAttempts([]);
      return;
    }
    setMathListLoading(true);
    try {
      const data = await getUserMathAttempts(userId, { days: filterDays });
      setMathAttempts(Array.isArray(data) ? data : []);
      setPageError(null);
      setExpandedMathAttemptId(null);
      setMathDetailsMap({});
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить math-попытки');
      setPageError(parsed);
    } finally {
      setMathListLoading(false);
    }
  };

  const loadGroups = async () => {
    setGroupsLoading(true);
    try {
      const list = await getAdminGroups();
      setGroups(Array.isArray(list) ? list : []);
      setPageError(null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить группы');
      setPageError(parsed);
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
      setPageError(null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить группы пользователя');
      setPageError(parsed);
      setUserGroupIds([]);
    }
  };

  useEffect(() => {
    if (userId) {
      loadSolutions();
      loadTestAttempts();
      loadImageSolutions();
      loadMathAttempts();
      loadUserGroups();
    }
    
  }, [filterDays, userId]);

  useEffect(() => {
    
    loadGroups();
    
  }, []);

  const displayedSolutions = useMemo(() => {
    const list = [...solutions];
    if (filterDays) {
      const since = new Date();
      since.setDate(since.getDate() - filterDays);
      
    }
    list.sort((a, b) => new Date(getSolutionDate(b) || 0) - new Date(getSolutionDate(a) || 0));
    return list;
  }, [solutions, filterDays]);

  const displayedAttempts = useMemo(() => {
    const list = [...testAttempts];
    list.sort((a, b) => new Date(getSolutionDate(b) || 0) - new Date(getSolutionDate(a) || 0));
    return list;
  }, [testAttempts]);

  const displayedImageSolutions = useMemo(() => {
    const list = [...(imageSolutions || [])];
    list.sort((a, b) => new Date(getImageSolutionDate(b) || 0) - new Date(getImageSolutionDate(a) || 0));
    return list;
  }, [imageSolutions]);

  const userGroupSet = useMemo(() => new Set(userGroupIds || []), [userGroupIds]);

  const splitFillPrompt = (prompt) => {
    const p = String(prompt || '');
    
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

    setExpandedId(id);
    if (!detailsMap[id]) {
      setDetailsLoadingMap((prev) => ({ ...prev, [id]: true }));
      try {
        const dto = await getSolutionDetails(id);
        setDetailsMap((prev) => ({ ...prev, [id]: dto }));
      } catch (e) {
        const parsed = handleApiError(e, notify, 'Не удалось загрузить детали решения');
        setPageError(parsed);
        setExpandedId(null);
        return;
      } finally {
        setDetailsLoadingMap((prev) => ({ ...prev, [id]: false }));
      }
    }
  };

  const handleToggleImageSolution = async (id) => {
    if (expandedImageId === id) {
      setExpandedImageId(null);
      return;
    }

    setExpandedImageId(id);
    if (!imageDetailsMap[id]) {
      setImageDetailsLoadingMap((prev) => ({ ...prev, [id]: true }));
      try {
        const dto = await getAdminImageSolutionDetails(id);
        setImageDetailsMap((prev) => ({ ...prev, [id]: dto }));
      } catch (e) {
        const parsed = handleApiError(e, notify, 'Не удалось загрузить детали image-решения');
        setPageError(parsed);
        setExpandedImageId(null);
        return;
      } finally {
        setImageDetailsLoadingMap((prev) => ({ ...prev, [id]: false }));
      }
    }
  };

  const displayedMathAttempts = useMemo(() => {
    const list = [...(mathAttempts || [])];
    list.sort((a, b) => new Date(getSolutionDate(b) || 0) - new Date(getSolutionDate(a) || 0));
    return list;
  }, [mathAttempts]);

  const handleToggleMathAttempt = async (attempt) => {
    const id = attempt.attemptId;
    if (expandedMathAttemptId === id) {
      setExpandedMathAttemptId(null);
      return;
    }

    if (!mathDetailsMap[id]) {
      try {
        const dto = await getAdminMathAttemptReview(id);
        setMathDetailsMap((prev) => ({ ...prev, [id]: dto }));
      } catch (e) {
        const parsed = handleApiError(e, notify, 'Не удалось загрузить детали math-попытки');
        setPageError(parsed);
        return;
      }
    }

    setExpandedMathAttemptId(id);
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
        const parsed = handleApiError(e, notify, 'Не удалось загрузить детали попытки теста');
        setPageError(parsed);
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
      notify.success('Решение удалено');
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось удалить решение');
      setPageError(parsed);
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
      notify.success('Попытка теста удалена');
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось удалить попытку теста');
      setPageError(parsed);
    }
  };

  const handleAddToGroup = async () => {
    if (!userId || !groupToAdd) return;
    try {
      await addGroupMember(groupToAdd, userId);
      setGroupToAdd('');
      await loadUserGroups();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось добавить пользователя в группу');
      setPageError(parsed);
    }
  };

  const handleRemoveFromGroup = async (groupId) => {
    if (!userId || !groupId) return;
    try {
      await removeGroupMember(groupId, userId);
      await loadUserGroups();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось удалить пользователя из группы');
      setPageError(parsed);
    }
  };


  const selectedUser = users.find((u) => u.id === userId) || null;

  return (
    <Layout>
      <div className="py-6 space-y-4 min-w-0">
        <h1 className="text-2xl font-semibold">Управление пользователями</h1>

        {pageError ? <AppErrorPanel error={pageError} title="Не удалось загрузить админ-раздел" /> : null}

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
            <Button variant={tab === 'math' ? 'primary' : 'outline'} onClick={() => setTab('math')}>
              Математика
            </Button>
            <Button variant={tab === 'groups' ? 'primary' : 'outline'} onClick={() => setTab('groups')}>
              Группы
            </Button>
          </div>

          <div className="grid gap-3 lg:grid-cols-[minmax(0,1.05fr)_minmax(0,1.25fr)_minmax(180px,0.55fr)_auto] items-end">
            <div className="space-y-1 min-w-0">
              <div className="text-xs uppercase tracking-wide text-neutral-500">
                Поиск пользователя
              </div>
              <div className="flex flex-wrap gap-2 sm:flex-nowrap min-w-0">
                <Input
                  className="min-w-0"
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

            <div className="space-y-1 min-w-0">
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

            <div className="space-y-1 min-w-0">
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

            <div className="flex flex-wrap items-end gap-2 min-w-0">
              <Button
                onClick={() => {
                  if (tab === 'tests') return loadTestAttempts();
                  if (tab === 'images') return loadImageSolutions();
                  if (tab === 'math') return loadMathAttempts();
                  if (tab === 'groups') return loadUserGroups();
                  return loadSolutions();
                }}
                disabled={!userId || listLoading || testListLoading || imageListLoading || mathListLoading || searchLoading}
              >
                {tab === 'tests'
                  ? 'Загрузить попытки тестов'
                  : tab === 'images'
                    ? 'Загрузить решения (картинки)'
                    : tab === 'math'
                      ? 'Загрузить math-попытки'
                      : tab === 'groups'
                        ? 'Обновить группы'
                        : 'Загрузить решения'}
              </Button>
              {tab === 'code' && (
                <Button intent="danger" onClick={handleDeleteAll} disabled={!userId || listLoading}>
                  Удалить все решения
                </Button>
              )}
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
        {tab === 'math' && mathListLoading && (
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
                const expanded = expandedId === item.id;
                const loadingDetails = !!detailsLoadingMap[item.id];
                const effective = full || item;
                const code = getSolutionCode(effective);
                const score = getSolutionScore(effective);
                const { passed, failed } = getSolutionPassedFailed(effective);

                return (
                  <div
                    key={item.id}
                    className="border border-neutral-200 dark:border-neutral-800/40 rounded-xl p-4 bg-[rgb(var(--card))]"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 mb-2">
                      <div className="min-w-0">
                        <div className="font-medium text-neutral-900 dark:text-neutral-50 truncate">
                          {getSolutionTitle(effective)}
                        </div>
                        <div className="text-xs text-neutral-600 dark:text-neutral-400">
                          {formatDateTime(getSolutionSubmittedAt(effective))} • {effective.language || effective.Language || '—'}
                        </div>
                      </div>
                      <div className="flex flex-wrap gap-2 items-center">
                        <Badge intent={getSolutionBadgeIntent(effective)}>{getSolutionStatusLabel(effective)}</Badge>
                        {score !== null ? <Badge intent="secondary">Score: {score}</Badge> : null}
                        {passed !== null || failed !== null ? (
                          <Badge intent="secondary">OK: {passed ?? 0} / FAIL: {failed ?? 0}</Badge>
                        ) : null}
                        <Button
                          variant="outline"
                          className="inline-flex items-center gap-2"
                          onClick={() => handleToggleCode(item.id)}
                          disabled={loadingDetails}
                        >
                          {expanded ? 'Скрыть код' : 'Показать код'}
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

                    {expanded ? (
                      <div className="mt-3 space-y-3">
                        {loadingDetails ? <CompactEmpty>Загружаю детали решения…</CompactEmpty> : null}
                        {!loadingDetails && full ? (
                          code ? (
                            <div className="rounded-xl overflow-hidden border border-neutral-700">
                              <CodeEditor
                                language={full.language || full.Language || item.language || 'text'}
                                value={code}
                                readOnly
                                onChange={() => {}}
                                height={360}
                              />
                            </div>
                          ) : (
                            <CompactEmpty>Код не найден для этого решения.</CompactEmpty>
                          )
                        ) : null}
                        {!loadingDetails && full ? <RunnerOutput item={full} /> : null}
                      </div>
                    ) : null}
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
                const expanded = expandedImageId === item.id;
                const loadingDetails = !!imageDetailsLoadingMap[item.id];
                const effective = full || item;
                const percent = getImageSolutionPercent(effective);
                const threshold = getImageSolutionThreshold(effective);
                const referenceUrl = getImageSolutionUrl(effective, 'reference');
                const submittedUrl = getImageSolutionUrl(effective, 'submitted');
                const code = getImageSolutionCode(effective);

                return (
                  <div
                    key={item.id}
                    className="border border-neutral-200 dark:border-neutral-800/40 rounded-xl p-4 bg-[rgb(var(--card))]"
                  >
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <div className="space-y-1 min-w-0">
                        <div className="font-medium truncate">
                          {getImageSolutionTitle(effective)}{' '}
                          <span className="text-sm text-neutral-500">
                            ({effective.kind || 'code'}{effective.isTrial ? ', пробник' : ''})
                          </span>
                        </div>
                        <div className="text-xs text-neutral-500">
                          {formatDateTime(getSolutionSubmittedAt(effective))} • {effective.language || effective.Language || '—'}
                        </div>
                      </div>

                      <div className="flex flex-wrap items-center gap-2">
                        {effective.passed === true ? (
                          <Badge intent="success">Зачёт</Badge>
                        ) : effective.passed === false ? (
                          <Badge intent="danger">Не зачтено</Badge>
                        ) : (
                          <Badge intent="secondary">Без сравнения</Badge>
                        )}

                        {percent !== null ? (
                          <Badge intent="secondary">
                            {Math.round(percent)}%{threshold !== null ? ` (порог ${Math.round(threshold)}%)` : ''}
                          </Badge>
                        ) : null}

                        <Button variant="outline" onClick={() => handleToggleImageSolution(item.id)} disabled={loadingDetails}>
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
                              notify.success('Image-решение удалено');
                            } catch (e) {
                              const parsed = handleApiError(e, notify, 'Не удалось удалить image-решение');
                              setPageError(parsed);
                            }
                          }}
                        >
                          <Trash2 className="w-4 h-4" />
                        </Button>
                      </div>
                    </div>

                    {expanded ? (
                      <div className="mt-4 space-y-4">
                        {loadingDetails ? <CompactEmpty>Загружаю детали image-решения…</CompactEmpty> : null}
                        {!loadingDetails && full ? (
                          <>
                            <div className="grid gap-4 md:grid-cols-2">
                              <div className="space-y-2">
                                <div className="text-xs uppercase tracking-wide text-neutral-500">Эталон</div>
                                {referenceUrl ? (
                                  <img src={referenceUrl} alt="reference" className="w-full rounded-lg border border-neutral-200 dark:border-neutral-700" />
                                ) : (
                                  <CompactEmpty>Эталон недоступен</CompactEmpty>
                                )}
                              </div>

                              <div className="space-y-2">
                                <div className="text-xs uppercase tracking-wide text-neutral-500">Результат</div>
                                {submittedUrl ? (
                                  <img src={submittedUrl} alt="submitted" className="w-full rounded-lg border border-neutral-200 dark:border-neutral-700" />
                                ) : (
                                  <CompactEmpty>Результат недоступен</CompactEmpty>
                                )}
                              </div>
                            </div>

                            {code ? (
                              <div className="space-y-2">
                                <div className="text-xs uppercase tracking-wide text-neutral-500">Код</div>
                                <CodeEditor value={code} language={full.language || full.Language || 'text'} readOnly />
                              </div>
                            ) : (
                              <CompactEmpty>Код не найден для этого image-решения.</CompactEmpty>
                            )}

                            <RunnerOutput item={full} />
                          </>
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
                          {getSolutionTitle(a)}
                        </div>
                        <div className="text-xs text-neutral-600 dark:text-neutral-400">
                          {formatDateTime(a.submittedAt)} • попытка #{a.attemptNumber}
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


        {tab === 'math' && !mathListLoading && displayedMathAttempts.length > 0 && (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-neutral-600 dark:text-neutral-300">
              Всего math-попыток: {displayedMathAttempts.length}
            </div>

            <div className="space-y-6">
              {displayedMathAttempts.map((a) => {
                const id = a.attemptId;
                const dto = mathDetailsMap[id] || null;
                const expanded = expandedMathAttemptId === id;

                return (
                  <div key={id} className="border border-neutral-200 dark:border-neutral-800/40 rounded-xl p-4 bg-[rgb(var(--card))]">
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                      <div>
                        <div className="font-medium text-neutral-900 dark:text-neutral-50">
                          {getSolutionTitle(a)}
                        </div>
                        <div className="text-xs text-neutral-600 dark:text-neutral-400">
                          {formatDateTime(a.submittedAt)} • попытка #{a.attemptNumber}
                        </div>
                      </div>
                      <div className="flex gap-2 items-center flex-wrap">
                        <Badge intent={a.passed ? 'success' : 'danger'}>{a.scorePercent}%</Badge>
                        <Badge intent="secondary">{a.earnedScore}/{a.totalScore}</Badge>
                        <Button variant="outline" className="inline-flex items-center gap-2" onClick={() => handleToggleMathAttempt(a)}>
                          {expanded ? 'Скрыть' : 'Просмотреть'}
                        </Button>
                        <Button
                          variant="outline"
                          intent="danger"
                          className="inline-flex items-center gap-2"
                          onClick={async () => {
                            const ok = window.confirm('Удалить эту math-попытку?');
                            if (!ok) return;
                            try {
                              await deleteAdminMathAttempt(id);
                              setMathAttempts((prev) => prev.filter((x) => x.attemptId !== id));
                              setMathDetailsMap((prev) => {
                                const copy = { ...prev };
                                delete copy[id];
                                return copy;
                              });
                              if (expandedMathAttemptId === id) setExpandedMathAttemptId(null);
                              notify.success('Math-попытка удалена');
                            } catch (e) {
                              const parsed = handleApiError(e, notify, 'Не удалось удалить math-попытку');
                              setPageError(parsed);
                            }
                          }}
                        >
                          <Trash2 size={16} />
                        </Button>
                      </div>
                    </div>

                    {expanded ? <MathAttemptReview dto={dto} admin /> : null}
                  </div>
                );
              })}
            </div>
          </Card>
        )}

        {tab === 'math' && !mathListLoading && !displayedMathAttempts.length && selectedUser && (
          <Card className="p-4 text-neutral-600 dark:text-neutral-400">
            Для этого пользователя нет math-попыток за выбранный период.
          </Card>
        )}

        {tab === 'groups' && selectedUser && (
          <Card className="p-4 space-y-4">
            <div className="flex items-center gap-2 text-sm text-neutral-600 dark:text-neutral-300">
              <Users size={18} />
              <span>Группы пользователя</span>
            </div>

            <div className="flex flex-wrap items-end gap-2">
              <div className="space-y-1 min-w-0">
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
