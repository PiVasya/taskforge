
import React, { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';

import Layout from '../components/Layout';
import QuotaPill from '../components/QuotaPill';
import { Card, Button, Select, Textarea, Badge } from '../components/ui';
import IfEditor from '../components/IfEditor';
import CodeEditor from '../components/CodeEditor';
import TaskTestSolve from './TaskTestSolve';
import MathTaskSolve from './MathTaskSolve';
import StatementViewer from '../components/tiptap/StatementViewer';

import { useNotify } from '../components/notify/NotifyProvider';
import { getAssignment, getAssignmentsByCourse } from '../api/assignments';
import { submitSolution } from '../api/solutions';
import { runImageTestCode, submitImageTestCode } from '../api/imageTests';
import { getAdminAssignmentInsights } from '../api/adminAssignmentInsights';
import { extractApiErrorMessages } from '../utils/handleApiError';
import { getApiErrorMessage } from '../api/http';

import { ArrowLeft, Play, CheckCircle2, XCircle, BarChart3 } from 'lucide-react';
import { useRoleFlags } from '../contexts/EditorModeContext';


const ALL_LANGS = [
  { value: 'cpp',        label: 'C++' },
  { value: 'csharp',     label: 'C#' },
  { value: 'javascript', label: 'JavaScript' },
  { value: 'pascal',     label: 'Pascal' },
  { value: 'java',       label: 'Java' },
];


function normalizeLang(x) {
  if (!x) return '';
  const s = String(x).trim().toLowerCase();

  if (s === 'c++' || s === 'cpp' || s === 'g++' || s === 'gcc' || s === 'cxx' || s === 'си++' || s === 'с++') return 'cpp';
  if (s === 'c#' || s === 'cs' || s === 'csharp' || s === 'sharp' || s === 'си#' || s === 'с#' || s === 'шарп') return 'csharp';
  if (s === 'js' || s === 'node' || s === 'nodejs' || s === 'node.js' || s === 'javascript' || s === 'java-script') return 'javascript';

  
  if (s === 'pas' || s === 'pascal' || s === 'pascalabc' || s === 'pascalabcnet') return 'pascal';

  
  if (s === 'java' || s === 'джава') return 'java';

  return s;
}





function parseAllowedLanguages(raw) {
  let arr = [];

  if (Array.isArray(raw)) arr = raw;
  else if (typeof raw === 'string') arr = raw.split(',').map(x => x.trim()).filter(Boolean);
  else arr = [];

  const allowed = arr
    .map(normalizeLang)
    .filter(Boolean);

  
  const allowedSet = new Set(allowed);
  const knownSet = new Set(ALL_LANGS.map(x => x.value));
  const filtered = Array.from(allowedSet).filter(x => knownSet.has(x));

  return filtered;
}

function buildImageTaskErrorText(err, fallbackMessage) {
  const parsed = extractApiErrorMessages(err, fallbackMessage);
  const lines = [parsed.primaryMessage];

  if (parsed.userHint && parsed.userHint !== parsed.primaryMessage) {
    lines.push(parsed.userHint);
  }

  for (const step of parsed.howToFix || []) {
    lines.push(`• ${step}`);
  }

  if (parsed.code) {
    lines.push(`Код ошибки: ${parsed.code}`);
  }

  return Array.from(new Set(lines.filter(Boolean))).join('\n');
}

function buildImageTaskResponseText(resp, fallbackMessage) {
  if (!resp || typeof resp !== 'object') return fallbackMessage;

  const primaryMessage =
    resp.message ||
    resp.error ||
    resp.runnerError ||
    fallbackMessage ||
    'Не удалось обработать ответ сервера';

  const lines = [primaryMessage];

  if (resp.detail && resp.detail !== primaryMessage) {
    lines.push(resp.detail);
  }

  if (resp.userHint && resp.userHint !== primaryMessage) {
    lines.push(resp.userHint);
  }

  for (const step of Array.isArray(resp.howToFix) ? resp.howToFix : []) {
    lines.push(`• ${step}`);
  }

  if (resp.code) {
    lines.push(`Код ошибки: ${resp.code}`);
  }

  return Array.from(new Set(lines.filter(Boolean))).join('\n');
}

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const nav = useNavigate();

  const notify = useNotify();
  const { isAdmin } = useRoleFlags();
  const [adminInsights, setAdminInsights] = useState(null);

  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);

  
  const [nextA, setNextA] = useState(null); 

  const [language, setLanguage] = useState('cpp');
  const [code, setCode] = useState('');
  const [plainMode, setPlainMode] = useState(false);

  
  const [codeSolveLayout, setCodeSolveLayout] = useState(
    () => localStorage.getItem('codeSolveLayout') || 'split'
  );

  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState(null); 

  
  const [imgBusy, setImgBusy] = useState(false);
  const [imgError, setImgError] = useState('');
  const [imgCompare, setImgCompare] = useState(null); 
  const [imgMode, setImgMode] = useState("code"); 
  const [imgIsRunning, setImgIsRunning] = useState(false);
  const [imageInput, setImageInput] = useState('');

  
  const allowedLangs = useMemo(() => {
    
    
    const raw =
      a?.allowedLanguages ??
      a?.allowedLanguagesCsv ??
      a?.courseAllowedLanguages ??
      a?.course?.allowedLanguages ??
      null;

    const parsed = parseAllowedLanguages(raw);

    
    if (String(a?.type || '').trim() === 'image-test') {
      return parsed.length > 0 ? parsed : ['pascal', 'cpp'];
    }

    return parsed;
  }, [a]);

  
  const langsForSelect = useMemo(() => {
    if (!allowedLangs || allowedLangs.length === 0) return ALL_LANGS;

    
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

        
        const parsedAllowed = parseAllowedLanguages(
          data?.allowedLanguages ??
          data?.courseAllowedLanguages ??
          data?.course?.allowedLanguages
        );
        
        const effectiveAllowed = (String(data?.type || '').trim() === 'image-test')
          ? (parsedAllowed.length > 0 ? parsedAllowed : ['pascal','cpp'])
          : parsedAllowed;

        let nextLang = defaultLangFromApi;

        if (effectiveAllowed.length > 0 && !effectiveAllowed.includes(nextLang)) {
          nextLang = effectiveAllowed[0];
        }

        setLanguage(nextLang);

        if (data?.starterCode) setCode(data.starterCode);
      } catch (e) {
        const msg = getApiErrorMessage(e, 'Не удалось загрузить задание');
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
    const onUi = () => setCodeSolveLayout(localStorage.getItem('codeSolveLayout') || 'split');
    window.addEventListener('tf-ui-settings-changed', onUi);
    return () => window.removeEventListener('tf-ui-settings-changed', onUi);
  }, []);

  
  
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
      const r = await submitSolution(assignmentId, { language, code });

      const cases = r?.cases ?? r?.testCases ?? r?.results ?? [];
      const policyCase = Array.isArray(cases)
        ? cases.find(c => (c?.status === 'policy_failed') || String(c?.compileStderr || c?.stderr || '').includes('[policy_failed]'))
        : null;
      const policyText = policyCase ? String(policyCase.compileStderr || policyCase.stderr || policyCase.error || '') : '';
      const allOk =
        (r?.passedAllTests === true) ||
        (r?.passedAll === true) ||
        (Array.isArray(cases) && cases.length > 0 && cases.every(c => c?.passed === true || c?.status === 'OK'));

      setResult({ ...r, __allPassed: allOk });

      if (allOk) {
        notify.success('Все тесты пройдены!');
      } else if (policyCase) {
        
        const lines = policyText
          .split('\n')
          .map(s => s.trim())
          .filter(Boolean)
          .filter(s => s.startsWith('- '))
          .slice(0, 4)
          .map(s => s.replace(/^\-\s*/, ''));
        const short = lines.length ? `: ${lines.join(' | ')}` : '';
        notify.error(`Отклонено анализатором кода${short}`);
      } else if (r?.compileError) {
        notify.error('Ошибка компиляции');
      } else {
        notify.error('Не все тесты пройдены');
      }

      try { localStorage.setItem(`results:${assignmentId}`, JSON.stringify({ result: r })); } catch {}
      window.open(`/assignment/${assignmentId}/results`, '_blank', 'noopener,noreferrer');
    } catch (e) {
      const msg = getApiErrorMessage(e, 'Не удалось отправить решение');
      setError(msg);
      notify.error(msg);
    } finally {
      setSubmitting(false);
    }
  };

  const extractPolicyUi = (resObj) => {
    if (!resObj) return null;
    const cs = resObj.cases ?? resObj.testCases ?? resObj.results ?? [];
    if (!Array.isArray(cs)) return null;
    const pc = cs.find(c => (c?.status === 'policy_failed') || String(c?.compileStderr || c?.stderr || '').includes('[policy_failed]'));
    if (!pc) return null;
    const txt = String(pc.compileStderr || pc.stderr || pc.error || '');
    const lines = txt.split('\n').map(s => s.trim()).filter(Boolean);
    const items = lines
      .filter(s => s.startsWith('- '))
      .map(s => s.replace(/^\-\s*/, ''));
    return {
      header: 'Решение отклонено анализатором кода',
      items,
      raw: txt,
    };
  };

  const policyUi = extractPolicyUi(result);

  useEffect(() => {
    let alive = true;
    (async () => {
      if (!isAdmin || !assignmentId) return;
      try {
        const stats = await getAdminAssignmentInsights(assignmentId);
        if (alive) setAdminInsights(stats);
      } catch {
        if (alive) setAdminInsights(null);
      }
    })();
    return () => { alive = false; };
  }, [assignmentId, isAdmin]);

  const renderAdminQuickInsights = () => {
    if (!isAdmin || !adminInsights) return null;
    const totalAttempts = (adminInsights.codeAttempts || 0) + (adminInsights.testAttempts || 0) + (adminInsights.imageAttempts || 0);
    const successRate = totalAttempts > 0 ? Math.round((adminInsights.successUsers || 0) / Math.max(adminInsights.uniqueUsers || 1, 1) * 100) : 0;
    return (
      <Card className="mb-6">
        <div className="flex items-center justify-between gap-3 mb-4">
          <div>
            <div className="font-semibold">Быстрая статистика задания</div>
            <div className="text-sm text-neutral-500 mt-1">Этот блок виден только администратору.</div>
          </div>
          <Link to={`/admin/assignments/${assignmentId}/insights`} className="btn-outline">
            <BarChart3 size={16} className="mr-2" /> Полная аналитика
          </Link>
        </div>
        <div className="grid sm:grid-cols-2 xl:grid-cols-4 gap-3">
          <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Пользователи</div><div className="text-2xl font-semibold mt-1">{adminInsights.uniqueUsers || 0}</div></div>
          <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Решили</div><div className="text-2xl font-semibold mt-1">{adminInsights.successUsers || 0}</div></div>
          <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Попытки</div><div className="text-2xl font-semibold mt-1">{totalAttempts}</div></div>
          <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Успешность</div><div className="text-2xl font-semibold mt-1">{successRate}%</div></div>
        </div>
      </Card>
    );
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

  
  if (a.type === 'test') {
    return (
      <Layout>
        
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
          <div className="flex items-center gap-2 flex-wrap">
            <QuotaPill bucket="tasks" />
            {isAdmin && (
              <Link to={`/admin/assignments/${a.id}/insights`} className="btn-outline">
                <BarChart3 size={16} className="mr-2" /> Аналитика задания
              </Link>
            )}
            <IfEditor>
              <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
                Редактировать
              </Link>
            </IfEditor>
          </div>
        </div>

        {renderAdminQuickInsights()}
        <TaskTestSolve assignment={a} assignmentId={a.id} />
      </Layout>
    );
  }


  
  if (a.type === 'math') {
    return (
      <Layout>
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
          <div className="flex items-center gap-2 flex-wrap">
            <QuotaPill bucket="tasks" />
            {isAdmin && (
              <Link to={`/admin/assignments/${a.id}/insights`} className="btn-outline">
                <BarChart3 size={16} className="mr-2" /> Аналитика задания
              </Link>
            )}
            <IfEditor>
              <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
                Редактировать
              </Link>
            </IfEditor>
          </div>
        </div>

        {renderAdminQuickInsights()}
        <MathTaskSolve assignment={a} assignmentId={a.id} />
      </Layout>
    );
  }

  
  if (a.type === 'image-test') {
    const expectedUrl = a.imageTestReferenceKey
      ? `/api/private-files/${encodeURIComponent(a.imageTestReferenceKey)}`
      : null;

    const imageLangs = langsForSelect.filter((l) => ['pascal', 'cpp'].includes(l.value));

    const openImageResultsUrl = (url) => {
      try { 
        window.open(url, '_blank'); 
      } catch (e) {
      }
    };

    const onTrialImageTest = async () => {
      setImgError(null);
      setImgCompare(null);
      setImgBusy(true);

      try {
        const resp = await runImageTestCode(assignmentId, language, code, imageInput);
        
        
        if (resp?.ok && resp?.renderedUrl) {
          setImgCompare({
            passed: null, 
            similarityPercent: null,
            thresholdPercent: null,
            expectedUrl: expectedUrl,
            actualUrl: resp.renderedUrl,
            isTrial: true,
          });
        } else {
          const errMsg = buildImageTaskResponseText(resp, 'Не удалось сгенерировать картинку');
          setImgError(errMsg);
        }
      } catch (e) {
        const errMsg = buildImageTaskErrorText(e, 'Не удалось выполнить пробный запуск');
        setImgError(errMsg);
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

      try {
        const resp = await submitImageTestCode(assignmentId, language, code, imageInput);
        
        
        if (resp?.ok) {
          setImgCompare({
            passed: resp.passed,
            similarityPercent: resp.similarityPercent,
            thresholdPercent: resp.thresholdPercent,
            expectedUrl: resp.referenceUrl || expectedUrl,
            actualUrl: resp.submittedUrl,
            isTrial: false,
          });
          
          
          if (resp.passed) {
            notify.success(`Задание выполнено! Схожесть: ${Math.round(resp.similarityPercent)}%`);
          } else {
            notify.warn(`Схожесть ${Math.round(resp.similarityPercent)}% < ${Math.round(resp.thresholdPercent)}%`);
          }
        } else {
          const errMsg = buildImageTaskResponseText(resp, 'Не удалось проверить решение');
          setImgError(errMsg);
        }
      } catch (e) {
        const errMsg = buildImageTaskErrorText(e, 'Не удалось отправить решение');
        setImgError(errMsg);
      } finally {
        setImgBusy(false);
      }
    };

    return (
      <Layout>
        
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
          <div className="flex items-center gap-2 flex-wrap">
            <QuotaPill bucket="tasks" />
            {isAdmin && (
              <Link to={`/admin/assignments/${a.id}/insights`} className="btn-outline">
                <BarChart3 size={16} className="mr-2" /> Аналитика задания
              </Link>
            )}
            <IfEditor>
              <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
                Редактировать
              </Link>
            </IfEditor>
          </div>
        </div>

        {renderAdminQuickInsights()}

        <div className="grid lg:grid-cols-3 gap-6">
          
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

          
          <div className="space-y-4">
            <Card>
              <div className="grid gap-3">
                <div>
                  <label className="label">Язык</label>
                  <Select
                    value={language}
                    onChange={(e) => setLanguage(e.target.value)}
                  >
                    {imageLangs.map((l) => (
                      <option key={l.value} value={l.value}>{l.label}</option>
                    ))}
                  </Select>
                  <div className="text-xs text-neutral-500 mt-1">
                    Для image-test доступны Python, Pascal и C++. Для C++ runner сам пытается снять скрин окна программы.
                  </div>
                </div>

                <div>
                  <label className="label">Код</label>
                  <div className="rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-700">
                    <CodeEditor
                      value={code}
                      onChange={setCode}
                      language={language}
                    />
                  </div>
                </div>

                <div>
                  <label className="label">Входные данные для программы</label>
                  <textarea
                    value={imageInput}
                    onChange={(e) => setImageInput(e.target.value)}
                    rows={4}
                    placeholder={language === 'cpp' ? 'Если программа читает stdin, введи данные сюда' : 'Необязательно. Можно оставить пустым.'}
                    className="w-full rounded-xl border border-neutral-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 px-3 py-2 text-sm"
                  />
                  <div className="text-xs text-neutral-500 mt-1">Для C++ можно оставить пустым. Раннер сам пытается снять скрин окна; если программа читает stdin, эти данные будут переданы в неё.</div>
                </div>

                {imgError ? (
                  <div className="text-rose-700 dark:text-rose-300 whitespace-pre-wrap">
                    {imgError}
                  </div>
                ) : null}

                
                {imgCompare && (
                  <Card className="p-4 space-y-3 border-emerald-400/30 bg-emerald-500/5">
                    <div className="flex items-center justify-between">
                      <h3 className="font-semibold">Результат</h3>
                      {imgCompare.isTrial ? (
                        <Badge variant="outline">Пробник</Badge>
                      ) : imgCompare.passed ? (
                        <Badge intent="success">Пройдено ✓</Badge>
                      ) : (
                        <Badge intent="danger">Не пройдено</Badge>
                      )}
                    </div>

                    {!imgCompare.isTrial && imgCompare.similarityPercent != null && (
                      <div className="text-sm">
                        <div>Схожесть: <strong>{Math.round(imgCompare.similarityPercent)}%</strong></div>
                        <div>Порог: <strong>{Math.round(imgCompare.thresholdPercent)}%</strong></div>
                      </div>
                    )}

                    <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                      {imgCompare.expectedUrl && (
                        <div>
                          <div className="text-xs font-medium mb-1">Эталон</div>
                          <img 
                            src={imgCompare.expectedUrl} 
                            alt="Эталон" 
                            className="w-full border border-neutral-300 dark:border-neutral-600 rounded"
                          />
                        </div>
                      )}
                      {imgCompare.actualUrl && (
                        <div>
                          <div className="text-xs font-medium mb-1">Ваш результат</div>
                          <img 
                            src={imgCompare.actualUrl} 
                            alt="Результат" 
                            className="w-full border border-neutral-300 dark:border-neutral-600 rounded"
                          />
                        </div>
                      )}
                    </div>
                  </Card>
                )}

                <Button
                  variant="outline"
                  onClick={() => {
                    const url = `/assignment/${assignmentId}/image-results`;
                    openImageResultsUrl(url);
                  }}
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

        
        <div
          className="fixed right-6 z-50"
          style={{ bottom: 'calc(env(safe-area-inset-bottom) + 84px)' }}
        >
          <div
            className="flex flex-col gap-2 rounded-2xl p-2 border shadow-lg w-56"
            style={{
              background: 'rgba(var(--card) / 0.60)',
              borderColor: 'rgba(var(--border) / 0.70)',
              backdropFilter: 'blur(14px)',
              WebkitBackdropFilter: 'blur(14px)',
            }}
          >
            {nextA?.id && (
              <Button
                className="w-full"
                variant="outline"
                onClick={goNextAssignment}
                title={nextA?.title || 'Следующее задание'}
              >
                Следующее задание
              </Button>
            )}
            <Button
              className="w-full"
              variant="outline"
              onClick={onTrialImageTest}
              disabled={imgBusy || !code.trim()}
            >
              {imgBusy ? 'Генерация картинки...' : 'Пробник'}
            </Button>
            <Button
              className="w-full"
              onClick={onSubmitImageTest}
              disabled={imgBusy || !code.trim() || !expectedUrl}
            >
              {imgBusy ? 'Отправка...' : 'Отправить (сравнение)'}
            </Button>
          </div>
        </div>
      </Layout>
    );
  }

const publicTests = (a.testCases || []).filter((t) => !t.isHidden);

  return (
    <Layout>
      
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
          {isAdmin && (
            <Link to={`/admin/assignments/${a.id}/insights`} className="btn-outline">
              <BarChart3 size={16} className="mr-2" /> Аналитика задания
            </Link>
          )}
          <IfEditor>
            <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
              Редактировать
            </Link>
          </IfEditor>
        </div>
      </div>

      {renderAdminQuickInsights()}

      
      {codeSolveLayout !== 'editorTop' ? (
        <div className="grid lg:grid-cols-3 gap-6">
          
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
                    <option value="editor">Редактор кода</option>
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

                {policyUi && (
                  <div className="rounded border border-red-200 bg-red-50 p-3 text-sm">
                    <div className="font-medium text-red-800 mb-2">{policyUi.header}</div>
                    {policyUi.items && policyUi.items.length > 0 ? (
                      <ul className="list-disc pl-5 text-red-800 space-y-1">
                        {policyUi.items.slice(0, 12).map((x, i) => (
                          <li key={i}>{x}</li>
                        ))}
                        {policyUi.items.length > 12 && (
                          <li>… и ещё {policyUi.items.length - 12}</li>
                        )}
                      </ul>
                    ) : (
                      <pre className="whitespace-pre-wrap text-xs text-red-700">{policyUi.raw}</pre>
                    )}
                  </div>
                )}

                {error && <div className="text-sm text-red-600">{error}</div>}
              </div>
            </Card>
          </div>
        </div>
      ) : (
        
        <div className="space-y-6">
          <Card>
            <div className="grid gap-3">
              <div className="grid gap-3 md:grid-cols-2">
                <div>
                  <label className="label">Язык</label>
                  <Select value={language} onChange={(e) => setLanguage(e.target.value)}>
                    {langsForSelect.map((l) => (
                      <option key={l.value} value={l.value}>{l.label}</option>
                    ))}
                  </Select>
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
                    <option value="editor">Редактор кода</option>
                    <option value="plain">Простой текст</option>
                  </Select>
                </div>
              </div>

              <div>
                <div className="flex items-center justify-between gap-3 mb-2">
                  <div className="font-semibold text-lg">{a.title}</div>
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
                </div>

                {policyUi && (
                  <div className="rounded border border-red-200 bg-red-50 p-3 text-sm mb-3">
                    <div className="font-medium text-red-800 mb-2">{policyUi.header}</div>
                    {policyUi.items && policyUi.items.length > 0 ? (
                      <ul className="list-disc pl-5 text-red-800 space-y-1">
                        {policyUi.items.slice(0, 12).map((x, i) => (
                          <li key={i}>{x}</li>
                        ))}
                        {policyUi.items.length > 12 && (
                          <li>… и ещё {policyUi.items.length - 12}</li>
                        )}
                      </ul>
                    ) : (
                      <pre className="whitespace-pre-wrap text-xs text-red-700">{policyUi.raw}</pre>
                    )}
                  </div>
                )}

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

                <label className="label">Ваш код</label>
                {plainMode ? (
                  <Textarea value={code} onChange={(e) => setCode(e.target.value)} rows={18} />
                ) : (
                  <CodeEditor language={language} value={code} onChange={setCode} height={460} />
                )}
              </div>

              {error && <div className="text-sm text-red-600">{error}</div>}
            </div>
          </Card>

          <Card>
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
      )}

      
      <div
        className="fixed right-6 z-50"
        style={{ bottom: 'calc(env(safe-area-inset-bottom) + 84px)' }}
      >
        <div
          className="flex flex-col gap-2 rounded-2xl p-2 border shadow-lg"
          style={{
            background: 'rgba(var(--card) / 0.60)',
            borderColor: 'rgba(var(--border) / 0.70)',
            backdropFilter: 'blur(14px)',
            WebkitBackdropFilter: 'blur(14px)',
          }}
        >
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
      </div>
    </Layout>
  );
}