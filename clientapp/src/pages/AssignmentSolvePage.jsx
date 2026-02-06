// src/pages/AssignmentSolvePage.jsx
import React, { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';

import Layout from '../components/Layout';
import { Card, Button, Select, Textarea, Badge } from '../components/ui';
import IfEditor from '../components/IfEditor';
import CodeEditor from '../components/CodeEditor';
import TaskTestSolve from './TaskTestSolve';
import StatementViewer from '../components/tiptap/StatementViewer';

import { useNotify } from '../components/notify/NotifyProvider';
import { getAssignment } from '../api/assignments';
import { submitSolution, listMySolutions } from '../api/solutions';
import { runTests as runCompilerTests } from '../api/compiler';
import { runImageTestCode, submitImageTestCode } from '../api/imageTests';

import { ArrowLeft, Play, CheckCircle2, XCircle } from 'lucide-react';

// ===== Все языки, которые поддерживает система =====
const ALL_LANGS = [
  { value: 'cpp',        label: 'C++' },
  { value: 'python',     label: 'Python' },
  { value: 'csharp',     label: 'C#' },
  { value: 'javascript', label: 'JavaScript' },
  { value: 'pascal',     label: 'Pascal' },
  { value: 'java',       label: 'Java' },
];

// Быстрая нормализация, чтобы понимать "C++", "c++", "js", "node", "c#" и т.п.
function normalizeLang(x) {
  if (!x) return '';
  const s = String(x).trim().toLowerCase();

  if (s === 'c++' || s === 'cpp') return 'cpp';
  if (s === 'c#' || s === 'cs' || s === 'csharp') return 'csharp';
  if (s === 'py' || s === 'python') return 'python';
  if (s === 'js' || s === 'node' || s === 'javascript') return 'javascript';

  // Pascal: можно расширять алиасы как угодно
  if (s === 'pas' || s === 'pascal' || s === 'pascalabc' || s === 'pascalabcnet') return 'pascal';

  // Java
  if (s === 'java') return 'java';

  return s;
}

// raw может быть:
// - массивом: ["cpp","python"]
// - строкой: "cpp, python, csharp"
// - null/undefined
function parseAllowedLanguages(raw) {
  let arr = [];

  if (Array.isArray(raw)) arr = raw;
  else if (typeof raw === 'string') arr = raw.split(',').map(x => x.trim()).filter(Boolean);
  else arr = [];

  const allowed = arr
    .map(normalizeLang)
    .filter(Boolean);

  // оставляем только те, которые вообще есть в ALL_LANGS
  const allowedSet = new Set(allowed);
  const knownSet = new Set(ALL_LANGS.map(x => x.value));
  const filtered = Array.from(allowedSet).filter(x => knownSet.has(x));

  return filtered;
}

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const nav = useNavigate();
  const notify = useNotify();

  const openInNewTab = (url) => {
    try {
      const w = window.open(url, '_blank', 'noopener,noreferrer');
      if (w) w.opener = null;
    } catch {
      // ignore
    }
  };

  const renderMyAttempts = () => (
    <div className="card mt-3">
      <div className="card-header">Мои попытки</div>
      <div className="card-body">
        {myAttemptsLoading ? (
          <div>Загрузка...</div>
        ) : myAttempts.length === 0 ? (
          <div className="text-muted">Пока нет попыток</div>
        ) : (
          <div className="list-group">
            {myAttempts.map((s) => (
              <button
                key={s.id}
                type="button"
                className="list-group-item list-group-item-action d-flex justify-content-between align-items-center"
                onClick={() => {
                  if (a?.type === 'image-test') {
                    openInNewTab(`/assignment/${assignmentId}/image-results?solutionId=${s.id}`);
                  } else {
                    openInNewTab(`/assignment/${assignmentId}/results?solutionId=${s.id}`);
                  }
                }}
              >
                <span>{new Date(s.createdAtUtc || s.createdAt || Date.now()).toLocaleString()}</span>
                <span className="badge bg-secondary">{s.passed === true ? 'OK' : s.passed === false ? 'FAIL' : ''}</span>
              </button>
            ))}
          </div>
        )}
      </div>
    </div>
  );


  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);

  const [language, setLanguage] = useState('cpp');
  const [code, setCode] = useState('');
  const [plainMode, setPlainMode] = useState(false);

  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState(null); // { results: [...], __allPassed?: bool }

  // image-test state
  const [imgBusy, setImgBusy] = useState(false);
  const [imgError, setImgError] = useState('');
  const [imgCompare, setImgCompare] = useState(null); // {percent, passed, expectedUrl, actualUrl}
  const [imgMode, setImgMode] = useState("code"); // code | upload
  const [imgIsRunning, setImgIsRunning] = useState(false);
  const [myAttempts, setMyAttempts] = useState([]);
  const [myAttemptsLoading, setMyAttemptsLoading] = useState(false);


  // Список языков, разрешённых для курса/задания (если есть ограничения)
  const allowedLangs = useMemo(() => {
    // Пытаемся найти ограничения в разных возможных полях,
    // чтобы фронт не падал, даже если поле назовёшь иначе.
    const raw =
      a?.allowedLanguages ??
      a?.allowedLanguagesCsv ??
      a?.courseAllowedLanguages ??
      a?.course?.allowedLanguages ??
      null;

    const parsed = parseAllowedLanguages(raw);

    // image-test: если ограничений нет — дефолт только python/pascal
    if (String(a?.type || '').trim() === 'image-test') {
      return parsed.length > 0 ? parsed : ['python', 'pascal'];
    }

    return parsed;
  }, [a]);

  // То, что показываем в Select
  const langsForSelect = useMemo(() => {
    if (!allowedLangs || allowedLangs.length === 0) return ALL_LANGS;

    // сохраняем порядок как в ALL_LANGS
    const set = new Set(allowedLangs);
    return ALL_LANGS.filter(x => set.has(x.value));
  }, [allowedLangs]);

  useEffect(() => {
    let alive = true;
    (async () => {
      setLoading(true);
      setError('');
      try {
        const data = await getAssignment(assignmentId);
        if (!alive) return;

        setA(data);

        const defaultLangFromApi = normalizeLang(data?.defaultLanguage) || 'cpp';

        // Если API прислал ограничения — применяем их
        const parsedAllowed = parseAllowedLanguages(
          data?.allowedLanguages ??
          data?.courseAllowedLanguages ??
          data?.course?.allowedLanguages
        );
        // image-test: если ограничений нет — дефолт только python/pascal
        const effectiveAllowed = (String(data?.type || '').trim() === 'image-test')
          ? (parsedAllowed.length > 0 ? parsedAllowed : ['python','pascal'])
          : parsedAllowed;

        let nextLang = defaultLangFromApi;

        if (effectiveAllowed.length > 0 && !effectiveAllowed.includes(nextLang)) {
          nextLang = effectiveAllowed[0];
        }

        setLanguage(nextLang);

        if (data?.starterCode) setCode(data.starterCode);
      } catch (e) {
        const msg = e?.response?.data?.error || e?.message || 'Не удалось загрузить задание';
        if (alive) {
          setError(msg);
          notify.error(msg);
        }
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => { alive = false; };
  }, [assignmentId]);

  useEffect(() => {
    if (!a?.id) return;
    setMyAttemptsLoading(true);
    (async () => {
      try {
        if (a.type === 'image-test') {
          const list = await getMyImageSolutions({ assignmentId: a.id, days: 365 });
          setMyAttempts(Array.isArray(list) ? list.slice(0, 10) : []);
        } else {
          const list = await listMySolutions({ assignmentId: a.id, skip: 0, take: 10 });
          setMyAttempts(Array.isArray(list) ? list : []);
        }
      } catch {
        setMyAttempts([]);
      } finally {
        setMyAttemptsLoading(false);
      }
    })();
  }, [a?.id, a?.type]);

  // Если ограничения изменились (например, подгрузились),
  // а выбранный язык теперь запрещён — переключаем на первый разрешённый.
  useEffect(() => {
    if (!allowedLangs || allowedLangs.length === 0) return;
    if (!allowedLangs.includes(language)) {
      setLanguage(allowedLangs[0]);
    }
  }, [allowedLangs, language]);

  const onSubmit = async () => {
    if (!code.trim()) return;
    setSubmitting(true);
    setError('');
    setResult(null);
    try {
      // SMOKE: первый публичный тест
      const firstPublic = (a?.testCases || []).find((t) => !t.isHidden);
      if (firstPublic) {
        try {
          const smoke = await runCompilerTests({ language, code, testCases: [firstPublic] });
          const scase = (smoke?.results || smoke?.testCases || smoke?.cases || [])[0] || {};
          const failed =
            Boolean(scase.error || scase.compileError || scase.stderr) ||
            scase.status === 'FAIL' || scase.passed === false || scase.ok === false;
          if (failed) {
            try { localStorage.setItem(`results:${assignmentId}`, JSON.stringify({ result: smoke })); } catch {}
            window.open(`/assignment/${assignmentId}/results?view=smoke`, '_blank', 'noopener,noreferrer');
            notify.error('Пробный прогон не прошёл. Детали — на странице результатов.');
            setSubmitting(false);
            return;
          }
        } catch (smokeErr) {
          const msg = smokeErr?.response?.data?.error || smokeErr?.message || 'Ошибка пробного прогона';
          const payload = { results: [{ compileStderr: msg }] };
          try { localStorage.setItem(`results:${assignmentId}`, JSON.stringify({ result: payload })); } catch {}
          window.open(`/assignment/${assignmentId}/results?view=smoke`, '_blank', 'noopener,noreferrer');
          notify.error(msg);
          setSubmitting(false);
          return;
        }
      }

      // Полный прогон
      const r = await submitSolution(assignmentId, { language, code });

      const cases = r?.cases ?? r?.testCases ?? r?.results ?? [];
      const allOk =
        (r?.passedAllTests === true) ||
        (r?.passedAll === true) ||
        (Array.isArray(cases) && cases.length > 0 && cases.every(c => c?.passed === true || c?.status === 'OK'));

      setResult({ ...r, __allPassed: allOk });

      if (allOk) notify.success('Все тесты пройдены!');
      else if (r?.compileError) notify.error('Ошибка компиляции');
      else notify.error('Не все тесты пройдены');

      try { localStorage.setItem(`results:${assignmentId}`, JSON.stringify({ result: r })); } catch {}
      window.open(`/assignment/${assignmentId}/results`, '_blank', 'noopener,noreferrer');
    } catch (e) {
      const msg = e?.response?.data?.error || e?.message || 'Не удалось отправить решение';
      setError(msg);
      notify.error(msg);
    } finally {
      setSubmitting(false);
    }
  };

  if (loading) {
    return (
      <Layout>
        <div className="text-slate-500">Загрузка…</div>
      </Layout>
    );
  }
  if (!a) {
    return (
      <Layout>
        <div className="text-red-600">{error || 'Задание не найдено'}</div>
      </Layout>
    );
  }

  // ===== Новый тип задания: тест =====
  if (a.type === 'test') {
    return (
      <Layout>
        {/* верхняя панель */}
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-2">
            <Link to={`/course/${a.courseId}`} className="text-brand-600 hover:underline flex items-center gap-1">
              <ArrowLeft size={16} /> к заданиям курса
            </Link>
          </div>
          <div className="flex items-center gap-2">
            <IfEditor>
              <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
                Редактировать
              </Link>
            </IfEditor>
          </div>
        </div>

        <TaskTestSolve assignment={a} assignmentId={a.id} />
      </Layout>
    );
  }

  // ===== Новый тип задания: image-test =====
  if (a.type === 'image-test') {
    const expectedUrl = a.imageTestReferenceKey
      ? `/api/private-files/${encodeURIComponent(a.imageTestReferenceKey)}`
      : null;

    const imageLangs = [
      { value: 'python', label: 'Python' },
      { value: 'pascal', label: 'Pascal' },
    ];

    // Открываем страницу результатов В НОВОЙ вкладке, как у обычных code-test.
    // Важно: чтобы браузер не блокировал попап, окно надо открыть синхронно (до await).
    const buildResultsUrl = (solutionId) => (
      solutionId
        ? `/assignment/${assignmentId}/image-results?solutionId=${encodeURIComponent(solutionId)}`
        : `/assignment/${assignmentId}/image-results`
    );
    const openResultsWindow = () => {
      try {
        const w = window.open('about:blank', '_blank', 'noopener,noreferrer');
        if (w) w.opener = null;
        return w;
      } catch {
        return null;
      }
    };


    const onTrialImageTest = async () => {
      const w = openResultsWindow();
      setImgError(null);
      setImgCompare(null);
      setImgBusy(true);
      try {
        const resp = await runImageTestCode(assignmentId, language, code, true);

        const payload = {
          ...resp,
          expectedUrl: expectedUrl,
          actualUrl: resp?.renderedUrl || resp?.submittedUrl || resp?.actualUrl,
          assignmentId,
          assignmentTitle: a.title,
          language,
          isTrial: true,
          createdAt: new Date().toISOString(),
        };

        localStorage.setItem(`image-results:${assignmentId}`, JSON.stringify(payload));
        const url = buildResultsUrl(resp?.solutionId);
        if (w && !w.closed) w.location.href = url;
        else window.open(url, '_blank');
      } catch (e) {
        setImgError(e?.response?.data?.message || e?.message || 'Ошибка выполнения');
        try { if (w && !w.closed) w.close(); } catch {}
      } finally {
        setImgBusy(false);
      }
    };

    const onSubmitImageTest = async () => {
      if (!expectedUrl) {
        setImgError('Эталонная картинка не настроена. Загрузите эталон в режиме редактирования задания.');
        return;
      }

      const w = openResultsWindow();

      setImgError(null);
      setImgCompare(null);
      setImgBusy(true);
      try {
        const resp = await submitImageTestCode(assignmentId, language, code, true);

        const payload = {
          ...resp,
          expectedUrl: resp?.referenceUrl || expectedUrl,
          actualUrl: resp?.submittedUrl || resp?.actualUrl,
          assignmentId,
          assignmentTitle: a.title,
          language,
          isTrial: false,
          createdAt: new Date().toISOString(),
        };

        localStorage.setItem(`image-results:${assignmentId}`, JSON.stringify(payload));
        const url = buildResultsUrl(resp?.solutionId);
        if (w && !w.closed) w.location.href = url;
        else window.open(url, '_blank');
      } catch (e) {
        setImgError(e?.response?.data?.message || e?.message || 'Ошибка выполнения');
        try { if (w && !w.closed) w.close(); } catch {}
      } finally {
        setImgBusy(false);
      }
    };

    return (
      <Layout>
        {/* верхняя панель — как у code-test */}
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-2">
            <Link to={`/course/${a.courseId}`} className="text-brand-600 hover:underline flex items-center gap-1">
              <ArrowLeft size={16} /> к заданиям курса
            </Link>
          </div>
          <div className="flex items-center gap-2">
            <IfEditor>
              <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
                Редактировать
              </Link>
            </IfEditor>
          </div>
        </div>

        <div className="grid lg:grid-cols-3 gap-6">
          {/* левая часть: текст задачи + эталон */}
          <div className="lg:col-span-2 space-y-5">
            <Card>
              <h1 className="text-2xl font-semibold mb-1">{a.title}</h1>
              {a.tags && (
                <div className="flex flex-wrap gap-2 mb-3">
                  {a.tags
                    .split(',')
                    .filter(Boolean)
                    .map((t) => (
                      <Badge key={t.trim()}>{t.trim()}</Badge>
                    ))}
                </div>
              )}
              <StatementViewer value={a.description} />
            </Card>

            <Card>
              <div className="flex items-center justify-between mb-3">
                <div className="font-medium">Эталон</div>
              </div>

              {expectedUrl ? (
                <div className="rounded border overflow-hidden bg-white dark:bg-slate-950">
                  <img
                    src={expectedUrl}
                    alt="Эталон"
                    className="w-full max-h-[70vh] object-contain"
                  />
                </div>
              ) : (
                <div className="text-slate-500">
                  Эталонная картинка не настроена. Открой «Редактировать» и нажми «Загрузить эталон».
                </div>
              )}
            </Card>
          </div>

          {/* правая часть: редактор и запуск — как у code-test */}
          <div className="space-y-4">
            <Card>
              <div className="grid gap-3">
                <div>
                  <label className="label">Язык</label>
                  <Select
                    value={['python', 'pascal'].includes(language) ? language : 'python'}
                    onChange={(e) => setLanguage(e.target.value)}
                  >
                    {imageLangs.map((l) => (
                      <option key={l.value} value={l.value}>{l.label}</option>
                    ))}
                  </Select>
                  <div className="text-xs text-slate-500 mt-1">
                    Для image-test доступны только Python и Pascal.
                  </div>
                </div>

                <div>
                  <label className="label">Код</label>
                  <div className="rounded-xl overflow-hidden border border-slate-200 dark:border-slate-700">
                    <CodeEditor
                      value={code}
                      onChange={setCode}
                      language={language === 'pascal' ? 'pascal' : 'python'}
                    />
                  </div>
                </div>

                {imgError ? (
                  <div className="text-rose-700 dark:text-rose-300 whitespace-pre-wrap">
                    {imgError}
                  </div>
                ) : null}

                <div className="flex flex-wrap gap-2">
                  <Button
                    variant="outline"
                    onClick={onTrialImageTest}
                    disabled={imgBusy || !code.trim()}
                  >
                    {imgBusy ? 'Выполняю…' : 'Пробник (только рендер)'}
                  </Button>

                  <Button
                    onClick={onSubmitImageTest}
                    disabled={imgBusy || !code.trim() || !expectedUrl}
                  >
                    {imgBusy ? 'Выполняю…' : 'Отправить (сравнение)'}
                  </Button>
                </div>

                <Button
                  variant="outline"
                  onClick={() => { if (latestSolutionId) openInNewTab(`/assignment/${assignmentId}/image-results?solutionId=${latestSolutionId}`); else openInNewTab(`/assignment/${assignmentId}/image-results`); }}
                >
                  Открыть последние результаты
                </Button>

                <div className="text-xs text-slate-500">
                  Пробник возвращает картинку без сравнения. Отправка выполняет сравнение с эталоном.
                </div>
              </div>
            </Card>
          </div>
        </div>
      </Layout>
    );
  }

const publicTests = (a.testCases || []).filter((t) => !t.isHidden);

  return (
    <Layout>
      {/* верхняя панель */}
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Link to={`/course/${a.courseId}`} className="text-brand-600 hover:underline flex items-center gap-1">
            <ArrowLeft size={16} /> к заданиям курса
          </Link>
        </div>
        <div className="flex items-center gap-2">
          <IfEditor>
            <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
              Редактировать
            </Link>
          </IfEditor>
        </div>
      </div>

      <div className="grid lg:grid-cols-3 gap-6">
        {/* левая часть: текст задачи + публичные тесты */}
        <div className="lg:col-span-2 space-y-5">
          <Card>
            <h1 className="text-2xl font-semibold mb-1">{a.title}</h1>
            {a.tags && (
              <div className="flex flex-wrap gap-2 mb-3">
                {a.tags
                  .split(',')
                  .filter(Boolean)
                  .map((t) => (
                    <Badge key={t.trim()}>{t.trim()}</Badge>
                  ))}
              </div>
            )}
            <StatementViewer value={a.description} />
          </Card>

          <Card>
            <div className="flex items-center justify-between mb-3">
              <div className="font-medium">Публичные тесты</div>
            </div>

            {publicTests.length === 0 ? (
              <div className="text-slate-500">У задания нет публичных тестов.</div>
            ) : (
              <div className="space-y-3">
                {publicTests.map((t, i) => {
                  const expectedText = t.expected ?? t.expectedOutput ?? t.ExpectedOutput ?? '';
                  return (
                    <div key={i} className="rounded border p-3">
                      <div className="text-xs text-slate-500 mb-1">Ввод</div>
                      <pre className="whitespace-pre-wrap text-sm">{t.input ?? t.Input ?? ''}</pre>

                      {(expectedText ?? '') !== '' && (
                        <>
                          <div className="text-xs text-slate-500 mt-2 mb-1">Ожидаемый вывод</div>
                          <pre className="whitespace-pre-wrap text-sm">{expectedText}</pre>
                        </>
                      )}
                    </div>
                  );
                })}
              </div>
            )}
          </Card>
        </div>

        {/* правая часть: редактор и запуск */}
        <div className="space-y-4">
          <Card>
            <div className="grid gap-3">
              <div>
                <label className="label">Язык</label>
                <Select value={language} onChange={(e) => setLanguage(e.target.value)}>
                  {langsForSelect.map((l) => (
                    <option key={l.value} value={l.value}>{l.label}</option>
                  ))}
                </Select>

                {/* маленькая подсказка, если ограничения включены */}
                {allowedLangs && allowedLangs.length > 0 && (
                  <div className="text-xs text-slate-500 mt-1">
                    Языки ограничены курсом: {langsForSelect.map(x => x.label).join(', ')}
                  </div>
                )}
              </div>

              <div>
                <label className="label">Режим ввода</label>
                <Select
                  value={plainMode ? 'plain' : 'editor'}
                  onChange={(e) => setPlainMode(e.target.value === 'plain')}
                >
                  <option value="editor">Графический редактор</option>
                  <option value="plain">Простой текст</option>
                </Select>
              </div>

              <div>
                <label className="label">Ваш код</label>
                {plainMode ? (
                  <Textarea value={code} onChange={(e) => setCode(e.target.value)} rows={16} />
                ) : (
                  <CodeEditor language={language} value={code} onChange={setCode} height={380} />
                )}
              </div>

              <div>
                <Button className="w-full" onClick={onSubmit} disabled={submitting || !code.trim()}>
                  <Play size={16} className="mr-1" />
                  {submitting ? 'Отправка…' : 'Отправить'}
                </Button>
              </div>

              {result && (
                <div className="flex items-center gap-2 text-sm">
                  {result.__allPassed ? (
                    <>
                      <CheckCircle2 className="text-emerald-600" size={16} /> Все тесты пройдены
                    </>
                  ) : (
                    <>
                      <XCircle className="text-red-600" size={16} /> Не все тесты пройдены
                    </>
                  )}
                </div>
              )}

              {error && <div className="text-sm text-red-600">{error}</div>}
            </div>
          </Card>
        {renderMyAttempts()}
        </div>
      </div>
    </Layout>
  );
}