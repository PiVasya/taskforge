import React, { useEffect, useMemo, useState } from 'react';
import { Card, Button, Input, Select, Badge } from '../../components/ui';
import { Trash2, Users, UserPlus, X } from 'lucide-react';
import {
  getSolutionDetails,
  getSolutionsDetailsBulkOrFallback,
  deleteUserSolutions,
  deleteSolution,
  getAdminImageSolutionDetails,
  deleteAdminImageSolution,
} from '../../api/admin';
import { getAdminTaskTestAttemptReview, deleteAdminTaskTestAttempt } from '../../api/taskTestAttempts';
import { addGroupMember, removeGroupMember } from '../../api/groups';
import { getAdminMathAttemptReview, deleteAdminMathAttempt } from '../../api/mathTaskAttempts';
import MathAttemptReview from '../../components/math/MathAttemptReview';
import CodeEditor from '../../components/CodeEditor';
import { useNotify } from '../../components/notify/NotifyProvider';
import AppErrorPanel from '../../components/AppErrorPanel';
import { handleApiError } from '../../utils/handleApiError';
import useAdminSolutionsData from './useAdminSolutionsData';
import useAdminSolutionLiveFeed from './useAdminSolutionLiveFeed';
import {
  filterLiveItems,
  filterLiveItemsByTab,
  solutionLiveStateLabel,
} from './adminSolutionLiveModel';
import { CompactEmpty, RunnerOutput, TestAttemptReview } from './components/AdminSolutionViews';
import AdminHistoryPager from './components/AdminHistoryPager';
import AdminCodeSolutionsPanel from './components/AdminCodeSolutionsPanel';
import AdminSolutionLiveCard, { AssignmentLinkButton } from './components/AdminSolutionLiveCard';
import {
  formatDateTime,
  getAssignmentId,
  getImageSolutionCode,
  getImageSolutionDate,
  getImageSolutionPercent,
  getImageSolutionThreshold,
  getImageSolutionTitle,
  getImageSolutionUrl,
  getSolutionDate,
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

export default function AdminSolutionsPage() {
  const notify = useNotify();
  const [pageError, setPageError] = useState(null);
  const [tab, setTab] = useState('live');

  const [q, setQ] = useState('');
  const [submittedQuery, setSubmittedQuery] = useState('');
  const [userId, setUserId] = useState('');
  const [groupId, setGroupId] = useState('');
  const [filterDays, setFilterDays] = useState(null);

  const [detailsMap, setDetailsMap] = useState({});
  const [detailsLoadingMap, setDetailsLoadingMap] = useState({});
  const [expandedCodeIds, setExpandedCodeIds] = useState([]);
  const [bulkCodeLoading, setBulkCodeLoading] = useState(false);
  const [historyPages, setHistoryPages] = useState({ code: 1, tests: 1, images: 1, math: 1 });
  const historyPageSize = 50;

  const [testDetailsMap, setTestDetailsMap] = useState({});
  const [expandedTestAttemptId, setExpandedTestAttemptId] = useState(null);

  const [imageDetailsMap, setImageDetailsMap] = useState({});
  const [imageDetailsLoadingMap, setImageDetailsLoadingMap] = useState({});
  const [expandedImageId, setExpandedImageId] = useState(null);

  const [mathDetailsMap, setMathDetailsMap] = useState({});
  const [expandedMathAttemptId, setExpandedMathAttemptId] = useState(null);

  const [groupToAdd, setGroupToAdd] = useState('');

  const {
    users,
    solutions,
    solutionsTotal,
    testAttempts,
    testAttemptsTotal,
    imageSolutions,
    imageSolutionsTotal,
    mathAttempts,
    mathAttemptsTotal,
    groups,
    userGroupIds,
    selectedGroupUserIds,
    searchLoading,
    listLoading,
    testListLoading,
    imageListLoading,
    mathListLoading,
    groupsLoading,
    groupMembersLoading,
    error: queryError,
    refetchUsers,
    refetchSolutions,
    refetchTests,
    refetchImages,
    refetchMath,
    refetchUserGroups,
    removeCodeSolution,
    removeTestAttempt,
    removeImageSolution,
    removeMathAttempt,
    setUserGroups,
  } = useAdminSolutionsData({
    searchQuery: submittedQuery,
    userId,
    groupId,
    filterDays,
    pages: historyPages,
    pageSize: historyPageSize,
  });

  const { items: liveItems, state: liveState } = useAdminSolutionLiveFeed();

  const loadUsers = () => {
    const next = q.trim();
    if (!next) {
      setSubmittedQuery('');
      setUserId('');
      return;
    }
    if (next === submittedQuery) {
      refetchUsers();
    } else {
      setUserId('');
      setGroupId('');
      setSubmittedQuery(next);
    }
  };

  useEffect(() => {
    setExpandedCodeIds([]);
    setHistoryPages({ code: 1, tests: 1, images: 1, math: 1 });
    setExpandedTestAttemptId(null);
    setExpandedImageId(null);
    setExpandedMathAttemptId(null);
    setDetailsMap({});
    setTestDetailsMap({});
    setImageDetailsMap({});
    setMathDetailsMap({});
    setGroupToAdd('');
  }, [filterDays, groupId, userId]);

  const displayedSolutions = useMemo(() => {
    const list = [...solutions];
    list.sort((a, b) => new Date(getSolutionDate(b) || 0) - new Date(getSolutionDate(a) || 0));
    return list;
  }, [solutions]);

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

  const displayedLiveItems = useMemo(() => filterLiveItems(liveItems, {
    userId,
    groupId,
    groupUserIds: selectedGroupUserIds,
    filterDays,
  }), [filterDays, groupId, liveItems, selectedGroupUserIds, userId]);

  const visibleLiveItems = useMemo(
    () => filterLiveItemsByTab(displayedLiveItems, tab),
    [displayedLiveItems, tab],
  );

  const liveStateLabel = solutionLiveStateLabel(liveState);

  const setHistoryPage = (kind, page) => {
    setHistoryPages((current) => ({ ...current, [kind]: Math.max(1, Number(page) || 1) }));
  };

  const handleToggleCode = async (id) => {
    const isExpanded = expandedCodeIds.includes(id);
    if (isExpanded) {
      setExpandedCodeIds((current) => current.filter((value) => value !== id));
      return;
    }

    setExpandedCodeIds((current) => (current.includes(id) ? current : [...current, id]));
    if (!detailsMap[id]) {
      setDetailsLoadingMap((prev) => ({ ...prev, [id]: true }));
      try {
        const dto = await getSolutionDetails(id);
        setDetailsMap((prev) => ({ ...prev, [id]: dto }));
      } catch (e) {
        const parsed = handleApiError(e, notify, 'Не удалось загрузить детали решения');
        setPageError(parsed);
        setExpandedCodeIds((current) => current.filter((value) => value !== id));
        return;
      } finally {
        setDetailsLoadingMap((prev) => ({ ...prev, [id]: false }));
      }
    }
  };

  const handleExpandPageCodes = async () => {
    const ids = displayedSolutions.map((item) => item.id).filter(Boolean);
    if (!ids.length) return;
    setBulkCodeLoading(true);
    try {
      const missingIds = ids.filter((id) => !detailsMap[id]);
      if (missingIds.length) {
        const details = await getSolutionsDetailsBulkOrFallback(missingIds, { concurrency: 6 });
        setDetailsMap((prev) => {
          const next = { ...prev };
          for (const dto of details) {
            const id = dto?.id || dto?.Id;
            if (id) next[id] = dto;
          }
          return next;
        });
      }
      setExpandedCodeIds((current) => Array.from(new Set([...current, ...ids])));
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить код решений');
      setPageError(parsed);
    } finally {
      setBulkCodeLoading(false);
    }
  };

  const handleCollapsePageCodes = () => {
    const pageIds = new Set(displayedSolutions.map((item) => item.id));
    setExpandedCodeIds((current) => current.filter((id) => !pageIds.has(id)));
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

  const handleDeleteAll = async () => {
    if (!userId) return;
    const ok = window.confirm('Удалить все решения выбранного пользователя?');
    if (!ok) return;
    await deleteUserSolutions(userId);
    await refetchSolutions();
  };

  const handleDeleteSolution = async (id) => {
    const ok = window.confirm('Удалить это решение (код)?');
    if (!ok) return;
    try {
      await deleteSolution(id);
      removeCodeSolution(id);
      setDetailsMap((prev) => {
        const copy = { ...prev };
        delete copy[id];
        return copy;
      });
      setExpandedCodeIds((current) => current.filter((value) => value !== id));
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
      removeTestAttempt(attemptId);
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
      const groupId = groupToAdd;
      await addGroupMember(groupId, userId);
      setUserGroups((current) => (current.includes(groupId) ? current : [...current, groupId]));
      setGroupToAdd('');
      await refetchUserGroups();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось добавить пользователя в группу');
      setPageError(parsed);
    }
  };

  const handleRemoveFromGroup = async (groupId) => {
    if (!userId || !groupId) return;
    try {
      await removeGroupMember(groupId, userId);
      setUserGroups((current) => current.filter((id) => id !== groupId));
      await refetchUserGroups();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось удалить пользователя из группы');
      setPageError(parsed);
    }
  };


  const selectedUser = users.find((u) => u.id === userId) || null;

  return (
    <>
      <div className="py-6 space-y-4 min-w-0">
        <h1 className="text-2xl font-semibold">Лента решений</h1>

        {pageError || queryError ? <AppErrorPanel error={pageError || queryError} title="Не удалось загрузить админ-раздел" /> : null}

        <Card className="p-4 space-y-3">
          <div className="flex flex-wrap items-center gap-2">
            <Button variant={tab === 'live' ? 'primary' : 'outline'} onClick={() => setTab('live')}>
              В эфире
            </Button>
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

          <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4 items-end">
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
              <Select value={userId} onChange={(e) => {
                const next = e.target.value;
                setUserId(next);
                if (next) setGroupId('');
              }}>
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
                Группа
              </div>
              <Select value={groupId} onChange={(e) => {
                const next = e.target.value;
                setGroupId(next);
                if (next) setUserId('');
              }}>
                <option value="">— все группы —</option>
                {groups.map((group) => (
                  <option key={group.id} value={group.id}>
                    {group.name || group.title || group.code || group.id}
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

            <div className="flex flex-wrap items-end gap-2 min-w-0 md:col-span-2 xl:col-span-4">
              {tab !== 'live' && <Button
                onClick={() => {
                  if (tab === 'tests') return refetchTests();
                  if (tab === 'images') return refetchImages();
                  if (tab === 'math') return refetchMath();
                  if (tab === 'groups') return refetchUserGroups();
                  return refetchSolutions();
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
              </Button>}
              {tab === 'code' && (
                <Button intent="danger" onClick={handleDeleteAll} disabled={!userId || listLoading}>
                  Удалить все решения
                </Button>
              )}
            </div>
          </div>

          {selectedUser && (
            <div
              className="flex flex-wrap items-center gap-2 text-xs text-neutral-600 dark:text-neutral-300"
              data-selected-user-id={selectedUser.id}
            >
              <span>Выбран: <span className="font-mono">{selectedUser.email}</span></span>
              <span className="font-mono opacity-70">ID: {selectedUser.id}</span>
              <Badge>Рейтинг: {selectedUser.score ?? selectedUser.rating ?? selectedUser.totalScore ?? 0}</Badge>
              <Badge>Решено: {selectedUser.solved ?? selectedUser.solvedCount ?? 0}</Badge>
            </div>
          )}
        </Card>

        {tab !== 'groups' && (
          <Card className="p-4 space-y-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div className="font-medium">Новые решения</div>
              <Badge intent={liveState === 'live' ? 'success' : 'secondary'}>{liveStateLabel}</Badge>
            </div>

            {groupMembersLoading ? (
              <div className="text-sm text-neutral-600 dark:text-neutral-300">Загружаю состав группы…</div>
            ) : null}

            {!groupMembersLoading && visibleLiveItems.length === 0 ? (
              <CompactEmpty>Новые решения появятся здесь сразу после отправки.</CompactEmpty>
            ) : null}

            <div className="space-y-2">
              {!groupMembersLoading && visibleLiveItems.map((item) => (
                <AdminSolutionLiveCard key={item.key} item={item} />
              ))}
            </div>
          </Card>
        )}

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
          <AdminCodeSolutionsPanel
            solutions={displayedSolutions}
            total={solutionsTotal}
            page={historyPages.code}
            pageSize={historyPageSize}
            onPage={(page) => setHistoryPage('code', page)}
            userId={userId}
            detailsMap={detailsMap}
            detailsLoadingMap={detailsLoadingMap}
            expandedCodeIds={expandedCodeIds}
            bulkCodeLoading={bulkCodeLoading}
            onExpandPageCodes={handleExpandPageCodes}
            onCollapsePageCodes={handleCollapsePageCodes}
            onToggleCode={handleToggleCode}
            onDeleteSolution={handleDeleteSolution}
          />
        )}

        
        {tab === 'images' && !imageListLoading && displayedImageSolutions.length > 0 && (
          <Card className="p-4 space-y-4">
            <AdminHistoryPager
              label="images"
              page={historyPages.images}
              total={imageSolutionsTotal}
              pageSize={historyPageSize}
              onPage={(page) => setHistoryPage('images', page)}
            />

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
                    data-solution-id={item.id}
                    data-user-id={item.userId || item.UserId || userId}
                    data-assignment-id={getAssignmentId(effective)}
                    data-verdict={effective.status || effective.verdict || ''}
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

                        <AssignmentLinkButton assignmentId={getAssignmentId(effective)} />
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
                              removeImageSolution(item.id);
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
            <AdminHistoryPager
              label="tests"
              page={historyPages.tests}
              total={testAttemptsTotal}
              pageSize={historyPageSize}
              onPage={(page) => setHistoryPage('tests', page)}
            />

            <div className="space-y-6">
              {displayedAttempts.map((a) => {
                const id = a.attemptId;
                const dto = testDetailsMap[id] || null;
                const expanded = expandedTestAttemptId === id;

                return (
                  <div
                    key={id}
                    className="border border-neutral-200 dark:border-neutral-800/40 rounded-xl p-4 bg-[rgb(var(--card))]"
                    data-attempt-id={id}
                    data-user-id={a.userId || a.UserId || userId}
                    data-assignment-id={getAssignmentId(a)}
                  >
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
                        <AssignmentLinkButton assignmentId={getAssignmentId(a)} />
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

                    {expanded ? <TestAttemptReview dto={dto} /> : null}
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
            <AdminHistoryPager
              label="math"
              page={historyPages.math}
              total={mathAttemptsTotal}
              pageSize={historyPageSize}
              onPage={(page) => setHistoryPage('math', page)}
            />

            <div className="space-y-6">
              {displayedMathAttempts.map((a) => {
                const id = a.attemptId;
                const dto = mathDetailsMap[id] || null;
                const expanded = expandedMathAttemptId === id;

                return (
                  <div
                    key={id}
                    className="border border-neutral-200 dark:border-neutral-800/40 rounded-xl p-4 bg-[rgb(var(--card))]"
                    data-attempt-id={id}
                    data-user-id={a.userId || a.UserId || userId}
                    data-assignment-id={getAssignmentId(a)}
                  >
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
                        <AssignmentLinkButton assignmentId={getAssignmentId(a)} />
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
                              removeMathAttempt(id);
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
    </>
  );
}
