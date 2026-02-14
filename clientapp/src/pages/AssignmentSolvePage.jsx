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
import { getAssignment, getAssignmentsByCourse } from '../api/assignments';
import { submitSolution } from '../api/solutions';
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

  // image-test: ссылка на страницу сравнения картинок (открывается в новой вкладке)
  const buildImageResultsUrl = React.useCallback((solutionId) => (
    solutionId
      ? `/assignment/${assignmentId}/image-results?solutionId=${encodeURIComponent(solutionId)}`
      : `/assignment/${assignmentId}/image-results`
  ), [assignmentId]);

  const openImageResults = React.useCallback((solutionId) => {
    const url = buildImageResultsUrl(solutionId);
    let w = null;
    try { w = window.open(url, '_blank'); } catch { w = null; }
    // Если попап заблокирован — открываем в текущей вкладке, чтобы пользователь всё равно увидел результаты
    if (!w) nav(url);
  }, [buildImageResultsUrl, nav]);
  const notify = useNotify();

  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);

  // Следующее задание в курсе (по Sort)
  const [nextA, setNextA] = useState(null); // {id,title} | null

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
  const [imgResultUrl, setImgResultUrl] = useState(''); // если попап заблокирован — даём кнопку

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

  // Вычисляем "следующее задание" в текущем курсе (по Sort).
  // Если текущего уже нет в списке — просто скрываем кнопку.
  useEffect(() => {
    let alive = true;
    (async () => {
      if (!a?.courseId || !a?.id) {
        if (alive) setNextA(null);
        return;
      }
      try {
        const list = await getAssignmentsByCourse(a.courseId);
        if (!alive) return;

        const ordered = (Array.isArray(list) ? list : [])
          .slice()
          .sort((x, y) => {
            const sx = Number(x?.sort ?? 0);
            const sy = Number(y?.sort ?? 0);
            if (sx !== sy) return sx - sy;
            return String(x?.title ?? '').localeCompare(String(y?.title ?? ''));
          });

        const idx = ordered.findIndex(x => String(x?.id) === String(a.id));
        const n = (idx >= 0) ? ordered[idx + 1] : null;
        if (n?.id) setNextA({ id: n.id, title: n.title || 'Следующее задание' });
        else setNextA(null);
      } catch {
        if (alive) setNextA(null);
      }
    })();
    return () => { alive = false; };
  }, [a?.courseId, a?.id]);

  const goNextAssignment = React.useCallback(() => {
    if (!nextA?.id) return;
    nav(`/assignment/${nextA.id}`);
  }, [nextA?.id, nav]);

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
        <div className="text-neutral-500">Загрузка…</div>
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
            <Button
              variant="ghost"
              className="inline-flex items-center gap-1"
              onClick={() => nav(`/course/${a.courseId}`)}
            >
              <ArrowLeft size={16} /> к заданиям курса
            </Button>
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

    const openImageResultPopup = () => {
      // Открываем вкладку строго в момент клика, иначе школьные браузеры часто блокируют
      const w = window.open('', '_blank', 'noopener,noreferrer');
      if (!w) return null;

      try {
        w.document.open();
        w.document.write(`<!doctype html><html><head><meta charset="utf-8" />
          <title>TaskForge — загрузка результата…</title>
          <style>
            body{font-family:system-ui,-apple-system,Segoe UI,Roboto,Arial;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
            .box{max-width:560px;padding:24px;text-align:center}
            .small{opacity:.7;margin-top:8px}
          </style>
        </head><body>
          <div class="box">
            <div>Готовим результат…</div>
            <div class="small">Окно обновится автоматически</div>
          </div>
        </body></html>`);
        w.document.close();
      } catch {
        // если доступ к document запрещён политиками — ничего страшного
      }

      return w;
    };

    const onTrialImageTest = async () => {
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
        const url = buildImageResultsUrl(resp?.solutionId);
        // Не используем window.open после await — в школах/строгих браузерах
        // попап-блокеры почти всегда режут открытие новой вкладки (получается about:blank).
        // Переходим на страницу результата в текущей вкладке.
        nav(url);
      } catch (e) {
        setImgError(e?.response?.data?.message || e?.message || 'Ошибка выполнения');
      } finally {
        setImgBusy(false);
      }
    };

    const onSubmitImageTest = async () => {
      if (!expectedUrl) {
        setImgError('Эталонная картинка не настроена. Загрузите эталон в режиме редактирования задания.');
        return;
      }

      setImgError(null);
      setImgCompare(null);
      setImgBusy(true);
      setImgResultUrl('');
      const popup = openImageResultPopup();
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
        const url = buildImageResultsUrl(resp?.solutionId);
        if (popup && !popup.closed) {
          try { popup.location.replace(url); } catch { popup.location.href = url; }
        } else {
          // Попап заблокирован или окно закрыто — покажем кнопку пользователю
          setImgResultUrl(url);
          notify.error('Браузер заблокировал новую вкладку. Нажмите "Открыть результат".');
        }
      } catch (e) {
        setImgError(e?.response?.data?.message || e?.message || 'Ошибка выполнения');
      } finally {
        setImgBusy(false);
      }
    };

    return (
      <Layout>
        {/* верхняя панель — как у code-test */}
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              className="inline-flex items-center gap-1"
              onClick={() => nav(`/course/${a.courseId}`)}
            >
              <ArrowLeft size={16} /> к заданиям курса
            </Button>
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
                <div className="rounded border overflow-hidden bg-white dark:bg-neutral-950">
                  <img
                    src={expectedUrl}
                    alt="Эталон"
                    className="w-full max-h-[70vh] object-contain"
                  />
                </div>
              ) : (
                <div className="text-neutral-500">
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
                  <div className="text-xs text-neutral-500 mt-1">
                    Для image-test доступны только Python и Pascal.
                  </div>
                </div>

                <div>
                  <label className="label">Код</label>
                  <div className="rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-700">
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

                <Button
                  variant="outline"
                  onClick={() => openImageResults(null)}
                >
                  Открыть последние результаты
                </Button>

                <div className="text-xs text-neutral-500">
                  Пробник возвращает картинку без сравнения. Отправка выполняет сравнение с эталоном.
                </div>
              </div>
            </Card>
          </div>
        </div>

        {/* Плавающие действия (как "Сохранить" в редакторе) */}
        <div className="fixed bottom-6 right-6 z-50 flex flex-col gap-2">
          {nextA?.id && (
            <Button variant="outline" onClick={goNextAssignment} title={nextA?.title || 'Следующее задание'}>
              Следующее задание
            </Button>
          )}
          <Button variant="outline" onClick={onTrialImageTest} disabled={imgBusy || !code.trim()}>
            {imgBusy ? 'Выполняю…' : 'Пробник'}
          </Button>
          <Button onClick={onSubmitImageTest} disabled={imgBusy || !code.trim() || !expectedUrl}>
            {imgBusy ? 'Выполняю…' : 'Отправить (сравнение)'}
          </Button>
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
          <Button
            variant="ghost"
            className="inline-flex items-center gap-1"
            onClick={() => nav(`/course/${a.courseId}`)}
          >
            <ArrowLeft size={16} /> к заданиям курса
          </Button>
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
              <div className="text-neutral-500">У задания нет публичных тестов.</div>
            ) : (
              <div className="space-y-3">
                {publicTests.map((t, i) => {
                  const expectedText = t.expected ?? t.expectedOutput ?? t.ExpectedOutput ?? '';
                  return (
                    <div key={i} className="rounded border p-3">
                      <div className="text-xs text-neutral-500 mb-1">Ввод</div>
                      <pre className="whitespace-pre-wrap text-sm">{t.input ?? t.Input ?? ''}</pre>

                      {(expectedText ?? '') !== '' && (
                        <>
                          <div className="text-xs text-neutral-500 mt-2 mb-1">Ожидаемый вывод</div>
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
                  <div className="text-xs text-neutral-500 mt-1">
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
        </div>
      </div>

      {/* Плавающие действия (как "Сохранить" в редакторе) */}
      <div className="fixed bottom-6 right-6 z-50 flex flex-col gap-2">
        {nextA?.id && (
          <Button variant="outline" onClick={goNextAssignment} title={nextA?.title || 'Следующее задание'}>
            Следующее задание
          </Button>
        )}
        <Button onClick={onSubmit} disabled={submitting || !code.trim()}>
          <Play size={16} className="mr-1" />
          {submitting ? 'Отправка…' : 'Отправить'}
        </Button>
      </div>
    </Layout>
  );
}