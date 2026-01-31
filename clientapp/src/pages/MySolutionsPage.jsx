// modified MySolutionsPage.jsx improves theme styling for solution cards
import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../components/Layout';
import { Card, Button, Badge } from '../components/ui';
import CodeEditor from '../components/CodeEditor';
import { getMySolutions, getMySolutionDetails } from '../api/solutions';
import { getMyTaskTestAttempts, getMyTaskTestAttemptReview } from '../api/taskTestAttempts';
import { getMyImageSolutions, getMyImageSolutionDetails } from '../api/imageSolutions';
import { useNotify } from '../components/notify/NotifyProvider';

const FILTER_OPTIONS = [
  { label: 'За всё время', value: null },
  { label: 'За сегодня', value: 1 },
  { label: 'За неделю', value: 7 },
  { label: 'За месяц', value: 30 },
];

export default function MySolutionsPage() {
  const notify = useNotify();

  const PAGE_SIZE = 50;

  const [tab, setTab] = useState('code');

  const [solutions, setSolutions] = useState([]);
  const [filterDays, setFilterDays] = useState(null);
  const [listLoading, setListLoading] = useState(false);
  const [solHasMore, setSolHasMore] = useState(true);
  const [solSkip, setSolSkip] = useState(0);
  const [details, setDetails] = useState({});
  const [expandedId, setExpandedId] = useState(null);

  const [testAttempts, setTestAttempts] = useState([]);
  const [testListLoading, setTestListLoading] = useState(false);
  const [testDetails, setTestDetails] = useState({});
  const [expandedTestAttemptId, setExpandedTestAttemptId] = useState(null);

  const [imageSolutions, setImageSolutions] = useState([]);
  const [imageListLoading, setImageListLoading] = useState(false);
  const [imageDetails, setImageDetails] = useState({});
  const [expandedImageId, setExpandedImageId] = useState(null);

  const loadSolutions = async ({ reset = false } = {}) => {
    setListLoading(true);
    try {
      const skip = reset ? 0 : solSkip;
      const list = await getMySolutions({ days: filterDays, skip, take: PAGE_SIZE });

      const arr = Array.isArray(list) ? list : [];

      if (reset) {
        setSolutions(arr);
        setSolSkip(arr.length);
      } else {
        setSolutions((prev) => [...prev, ...arr]);
        setSolSkip((prev) => prev + arr.length);
      }

      // если пришло меньше PAGE_SIZE — страниц больше нет
      setSolHasMore(arr.length === PAGE_SIZE);
    } catch (e) {
      console.error('Failed to load my solutions', e);
    } finally {
      setListLoading(false);
    }
  };

  const loadTestAttempts = async () => {
    setTestListLoading(true);
    try {
      const list = await getMyTaskTestAttempts({ days: filterDays });
      setTestAttempts(Array.isArray(list) ? list : []);
    } catch (e) {
      console.error('Failed to load my test attempts', e);
    } finally {
      setTestListLoading(false);
    }
  };

  const loadImageSolutions = async () => {
    setImageListLoading(true);
    try {
      const list = await getMyImageSolutions({ days: filterDays });
      setImageSolutions(Array.isArray(list) ? list : []);
    } catch (e) {
      console.error('Failed to load my image solutions', e);
    } finally {
      setImageListLoading(false);
    }
  };

  useEffect(() => {
    // При смене фильтра начинаем с первой страницы.
    setSolSkip(0);
    setSolHasMore(true);
    loadSolutions({ reset: true });
    loadTestAttempts();
    loadImageSolutions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterDays]);

  // Для типа "fill" (вставить пропущенное слово) — поле ввода прямо в тексте.
  const splitFillPrompt = (prompt) => {
    const p = String(prompt || '');
    // 3+ чтобы не ловить _ в идентификаторах кода.
    const m = p.match(/_{3,}/);
    if (!m) return null;
    const blank = m[0];
    const i = p.indexOf(blank);
    return { before: p.slice(0, i), after: p.slice(i + blank.length), blankLen: blank.length };
  };

  const displayedSolutions = useMemo(() => {
    const list = [...solutions];
    list.sort((a, b) => new Date(b.submittedAt) - new Date(a.submittedAt));
    return list;
  }, [solutions]);

  const displayedImageSolutions = useMemo(() => {
    const list = [...imageSolutions];
    list.sort((a, b) => new Date(b.createdAtUtc) - new Date(a.createdAtUtc));
    return list;
  }, [imageSolutions]);

  const handleToggleImageSolution = async (id) => {
    if (expandedImageId === id) {
      setExpandedImageId(null);
      return;
    }

    if (!imageDetails[id]) {
      try {
        const full = await getMyImageSolutionDetails(id);
        setImageDetails((prev) => ({ ...prev, [id]: full }));
      } catch (e) {
        console.error('Failed to load image solution details', e);
        notify.error('Не удалось загрузить решение по картинке');
        return;
      }
    }

    setExpandedImageId(id);
  };

  const handleToggleCode = async (id) => {
    if (expandedId === id) {
      setExpandedId(null);
      return;
    }
    if (!details[id]) {
      try {
        const full = await getMySolutionDetails(id);
        setDetails((prev) => ({ ...prev, [id]: full }));
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

    if (attempt.allowReview === false) {
      notify.warn('Просмотр результатов для этого теста отключён');
      return;
    }

    if (!testDetails[id]) {
      try {
        const dto = await getMyTaskTestAttemptReview(id);
        setTestDetails((p) => ({ ...p, [id]: dto }));
      } catch (e) {
        if (e?.response?.status === 403) {
          notify.warn('Просмотр результатов для этого теста отключён');
        } else {
          console.error('Failed to load attempt review', e);
          notify.error('Не удалось загрузить просмотр попытки');
        }
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
                          {isSel && !isCorr ? <span className="text-xs opacity-80">(ваш выбор)</span> : null}
                        </div>
                      );
                    })}
                  </div>
                )}

                {(type === 'text' || type === 'fill') && !split && (
                  <div className="mt-3 space-y-2 text-sm">
                    <div>
                      <span className="text-slate-500 dark:text-slate-400">Ваш ответ:</span> {userText || <i>—</i>}
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

  const displayedAttempts = useMemo(() => {
    const list = [...testAttempts];
    list.sort((a, b) => new Date(b.submittedAt) - new Date(a.submittedAt));
    return list;
  }, [testAttempts]);

  return (
    <Layout>
      <div className="container-app py-6 space-y-4">
        <h1 className="text-2xl font-semibold">Мои решения</h1>

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
          </div>

          <div className="flex flex-wrap gap-2 items-center">
            <span className="label">Период:</span>
            {FILTER_OPTIONS.map((opt) => (
              <Button
                key={opt.label}
                variant={filterDays === opt.value ? 'primary' : 'outline'}
                onClick={() => setFilterDays(opt.value)}
              >
                {opt.label}
              </Button>
            ))}
          </div>

          {tab === 'code' && listLoading && <div className="text-slate-500 dark:text-slate-400">Загрузка…</div>}
          {tab === 'tests' && testListLoading && <div className="text-slate-500 dark:text-slate-400">Загрузка…</div>}
          {tab === 'images' && imageListLoading && <div className="text-slate-500 dark:text-slate-400">Загрузка…</div>}

          {tab === 'code' && !listLoading && !displayedSolutions.length && (
            <div className="text-slate-500 dark:text-slate-400">За выбранный период решений нет.</div>
          )}
          {tab === 'tests' && !testListLoading && !displayedAttempts.length && (
            <div className="text-slate-500 dark:text-slate-400">За выбранный период попыток тестов нет.</div>
          )}
          {tab === 'images' && !imageListLoading && !displayedImageSolutions.length && (
            <div className="text-slate-500 dark:text-slate-400">За выбранный период решений по картинкам нет.</div>
          )}
        </Card>

        {tab === 'code' && displayedSolutions.length > 0 && (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-slate-500 dark:text-slate-400 mb-2">
              Показано решений по коду: {displayedSolutions.length}
            </div>

            <div className="space-y-6">
              {displayedSolutions.map((item) => {
                const full = details[item.id] || null;
                const showCode = expandedId === item.id && full;
                return (
                  <div
                    key={item.id}
                    className="border border-slate-200 dark:border-slate-700 rounded-xl p-4 bg-[rgb(var(--card))]"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 mb-2">
                      <div>
                        <div className="font-medium">
                          {item.courseTitle} • {item.assignmentTitle}
                        </div>
                        <div className="text-xs text-slate-500 dark:text-slate-400">
                          {new Date(item.submittedAt).toLocaleString()} • {item.language}
                        </div>
                      </div>
                      <div className="flex gap-2 items-center">
                        {item.passedAllTests ? (
                          <Badge intent="success">Все тесты пройдены ({item.passedCount})</Badge>
                        ) : (
                          <Badge intent="danger">Провалено: {item.failedCount} / Пройдено: {item.passedCount}</Badge>
                        )}
                        <Button onClick={() => handleToggleCode(item.id)}>
                          {expandedId === item.id ? 'Скрыть код' : 'Показать код'}
                        </Button>
                      </div>
                    </div>
                    {showCode && (
                      <div className="mt-3 rounded-xl overflow-hidden border border-slate-200 dark:border-slate-700">
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

            {solHasMore && (
              <div className="pt-2 flex justify-center">
                <Button
                  variant="outline"
                  onClick={() => loadSolutions({ reset: false })}
                  disabled={listLoading}
                >
                  {listLoading ? 'Загрузка…' : 'Загрузить ещё'}
                </Button>
              </div>
            )}
          </Card>
        )}

        {tab === 'tests' && !testListLoading && displayedAttempts.length > 0 && (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-slate-500 dark:text-slate-400 mb-2">
              Всего попыток тестов: {displayedAttempts.length}
            </div>

            <div className="space-y-6">
              {displayedAttempts.map((a) => {
                const id = a.attemptId;
                const dto = testDetails[id] || null;
                const expanded = expandedTestAttemptId === id;
                return (
                  <div key={id} className="border border-slate-200 dark:border-slate-700 rounded-xl p-4 bg-[rgb(var(--card))]">
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                      <div>
                        <div className="font-medium">{a.courseTitle} • {a.assignmentTitle}</div>
                        <div className="text-xs text-slate-500 dark:text-slate-400">
                          {new Date(a.submittedAt).toLocaleString()} • попытка #{a.attemptNumber}
                        </div>
                      </div>
                      <div className="flex gap-2 items-center">
                        <Badge intent={a.passed ? 'success' : 'danger'}>{a.scorePercent}%</Badge>
                        {a.allowReview === false ? <Badge intent="secondary">Просмотр скрыт</Badge> : null}
                        {a.allowReview !== false ? (
                          <Button
                            variant="primary"
                            onClick={() => handleToggleTestAttempt(a)}
                          >
                            {expanded ? 'Скрыть' : 'Просмотреть'}
                          </Button>
                        ) : null}
                      </div>
                    </div>

                    {expanded ? renderAttemptReview(dto) : null}
                  </div>
                );
              })}
            </div>
          </Card>
        )}

        {tab === 'images' && !imageListLoading && displayedImageSolutions.length > 0 && (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-slate-500 dark:text-slate-400 mb-2">
              Всего решений по картинкам: {displayedImageSolutions.length}
            </div>

            <div className="space-y-6">
              {displayedImageSolutions.map((it) => {
                const full = imageDetails[it.id] || null;
                const expanded = expandedImageId === it.id && full;
                const dt = new Date(it.createdAtUtc).toLocaleString();

                const openResult = () => {
                  window.open(`/assignment/${it.assignmentId}/image-results?solutionId=${it.id}`, '_blank');
                };

                return (
                  <div key={it.id} className="border border-slate-200 dark:border-slate-700 rounded-xl p-4 bg-[rgb(var(--card))]">
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                      <div>
                        <div className="font-medium">{it.assignmentTitle}</div>
                        <div className="text-xs text-slate-500 dark:text-slate-400">
                          {dt}
                          {it.language ? ` • ${it.language}` : ''}
                        </div>
                      </div>
                      <div className="flex flex-wrap gap-2 items-center">
                        {it.isTrial ? <Badge intent="secondary">Пробник</Badge> : null}
                        {it.passed === true ? <Badge intent="success">Пройдено</Badge> : null}
                        {it.passed === false ? <Badge intent="danger">Не пройдено</Badge> : null}
                        {typeof it.similarityPercent === 'number' ? (
                          <Badge intent={it.passed ? 'success' : 'danger'}>
                            {Math.round(it.similarityPercent * 10) / 10}%
                          </Badge>
                        ) : null}
                        <Button variant="outline" onClick={openResult}>Открыть</Button>
                        <Button onClick={() => handleToggleImageSolution(it.id)}>
                          {expandedImageId === it.id ? 'Скрыть' : 'Подробнее'}
                        </Button>
                      </div>
                    </div>

                    {expanded ? (
                      <div className="mt-4 space-y-4">
                        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
                          <Card className="p-3">
                            <div className="text-sm font-medium mb-2">Эталон</div>
                            {full.referenceUrl ? (
                              <img src={full.referenceUrl} alt="Эталон" className="w-full rounded-lg border" />
                            ) : (
                              <div className="text-sm text-slate-500 dark:text-slate-400">—</div>
                            )}
                          </Card>
                          <Card className="p-3">
                            <div className="text-sm font-medium mb-2">Результат</div>
                            {full.submittedUrl ? (
                              <img src={full.submittedUrl} alt="Результат" className="w-full rounded-lg border" />
                            ) : (
                              <div className="text-sm text-slate-500 dark:text-slate-400">—</div>
                            )}
                          </Card>
                        </div>

                        {full.submittedCode ? (
                          <div className="rounded-xl overflow-hidden border border-slate-200 dark:border-slate-700">
                            <CodeEditor
                              language={full.language || it.language || 'text'}
                              value={full.submittedCode}
                              readOnly
                              onChange={() => {}}
                              height={320}
                            />
                          </div>
                        ) : null}

                        {(full.stdout || full.stderr || full.runnerError) ? (
                          <Card className="p-3 space-y-2">
                            {full.runnerError ? (
                              <div className="text-sm text-rose-700 dark:text-rose-300">{full.runnerError}</div>
                            ) : null}
                            {full.stdout ? (
                              <div>
                                <div className="text-xs text-slate-500 dark:text-slate-400">stdout</div>
                                <pre className="text-xs whitespace-pre-wrap break-words mt-1">{full.stdout}</pre>
                              </div>
                            ) : null}
                            {full.stderr ? (
                              <div>
                                <div className="text-xs text-slate-500 dark:text-slate-400">stderr</div>
                                <pre className="text-xs whitespace-pre-wrap break-words mt-1">{full.stderr}</pre>
                              </div>
                            ) : null}
                          </Card>
                        ) : null}
                      </div>
                    ) : null}
                  </div>
                );
              })}
            </div>
          </Card>
        )}
      </div>
    </Layout>
  );
}