import React, { useEffect, useState } from 'react';
import { Link, useParams, useNavigate, useSearchParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card, Button, Badge } from '../components/ui';
import { getAssignment } from '../api/assignments';
import { getMySolutionDetails } from '../api/solutions';
import { ArrowLeft, RotateCcw } from 'lucide-react';

function displayClean(s) {
  if (s == null) return '';
  return String(s);
}

function parsePolicyText(raw) {
  const txt = String(raw || '');
  if (!txt) return null;
  const isPolicy = txt.includes('[policy_failed]') || txt.toLowerCase().includes('code analyzer blocked');
  if (!isPolicy) return null;

  const lines = txt.split('\n').map(s => s.trim()).filter(Boolean);

  const forbidden = [];
  const required = [];
  const other = [];
  const hits = [];

  let inHits = false;
  for (const l of lines) {
    if (l.startsWith('[hits]')) { inHits = true; continue; }
    if (l.startsWith('[') && l.endsWith(']')) { inHits = false; continue; }
    if (!l.startsWith('- ')) continue;
    const body = l.replace(/^\-\s*/, '');
    if (inHits) {
      
      const mNeedle = body.match(/needle=\'?([^'\s]+)\'?/i);
      const mPos = body.match(/pos=(\d+)/i);
      const mPrev = body.match(/preview=\'([^']*)\'/i);
      hits.push({
        needle: mNeedle?.[1],
        pos: mPos?.[1],
        preview: mPrev?.[1],
      });
      continue;
    }

    
    
    const cleaned = body
      .replace(/\(pattern_id=[^)]+\)/gi, '')
      .replace(/\s{2,}/g, ' ')
      .trim();

    if (/\bforbidden\b|Запрещено/i.test(cleaned)) forbidden.push(cleaned.replace(/^forbidden_[^:]*:\s*/i, ''));
    else if (/required\b|обязател/i.test(cleaned)) required.push(cleaned.replace(/^missing_[^:]*:\s*/i, ''));
    else other.push(cleaned);
  }

  const bullets = [];
  if (forbidden.length) {
    bullets.push(`❌ Запрещено в этом задании: ${forbidden.join(' • ')}`);
  }
  if (required.length) {
    bullets.push(`✅ Нужно обязательно использовать: ${required.join(' • ')}`);
  }
  if (!forbidden.length && !required.length && other.length) {
    bullets.push(...other);
  }

  const hitLines = hits
    .filter(h => h.needle || h.pos || h.preview)
    .slice(0, 3)
    .map(h => {
      const parts = [];
      if (h.needle) parts.push(`«${h.needle}»`);
      if (h.pos) parts.push(`позиция ${h.pos}`);
      if (h.preview) parts.push(`фрагмент: ${h.preview}`);
      return `• Найдено ${parts.join(', ')}`;
    });

  return {
    title: 'Решение не принято: анализатор кода нашёл нарушение',
    bullets,
    hitLines,
    raw: txt,
  };
}

export default function AssignmentResultsPage() {
  const { assignmentId } = useParams();
  const nav = useNavigate();
  const [searchParams] = useSearchParams();
  const view = searchParams.get('view') || 'full';
  const solutionId = searchParams.get('solutionId') || null;

  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);
  const [res, setRes] = useState(null);

  useEffect(() => {
    let alive = true;
    (async () => {
      setLoading(true);
      try {
        const data = await getAssignment(assignmentId);
        if (!alive) return;
        setA(data);
      } catch {
        if (alive) setA(null);
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => { alive = false; };
  }, [assignmentId]);

  useEffect(() => {
    let alive = true;

    async function loadResult() {
      if (solutionId) {
        try {
          const dto = await getMySolutionDetails(solutionId);
          if (!alive) return;
          setRes(dto || null);
          try { localStorage.setItem(`results:${assignmentId}`, JSON.stringify({ result: dto })); } catch {}
          return;
        } catch {
          // Fallback to the last local submit result below.
        }
      }

      const raw = localStorage.getItem(`results:${assignmentId}`);
      if (raw) {
        try {
          const { result } = JSON.parse(raw);
          if (alive) setRes(result);
        } catch {
          if (alive) setRes(null);
        }
      } else if (alive) {
        setRes(null);
      }
    }

    loadResult();
    return () => { alive = false; };
  }, [assignmentId, solutionId]);

  useEffect(() => {
    const id = solutionId || res?.id || res?.Id;
    if (!id) return undefined;

    const status = String(res?.status || res?.verdict || '').toLowerCase();
    const pending = res?.isPending === true || res?.result?.pending === true || ['preparing', 'queued', 'running', 'pending'].includes(status);
    if (!pending) return undefined;

    let alive = true;
    let timer = null;

    const poll = async () => {
      try {
        const dto = await getMySolutionDetails(id);
        if (!alive) return;
        setRes(dto || null);
        try { localStorage.setItem(`results:${assignmentId}`, JSON.stringify({ result: dto })); } catch {}
        const nextStatus = String(dto?.status || dto?.verdict || '').toLowerCase();
        const nextPending = dto?.isPending === true || dto?.result?.pending === true || ['preparing', 'queued', 'running', 'pending'].includes(nextStatus);
        if (nextPending) timer = setTimeout(poll, 1500);
      } catch {
        if (alive) timer = setTimeout(poll, 2500);
      }
    };

    timer = setTimeout(poll, 1500);
    return () => {
      alive = false;
      if (timer) clearTimeout(timer);
    };
  }, [assignmentId, solutionId, res?.id, res?.Id, res?.status, res?.verdict, res?.isPending, res?.result?.pending]);

  const handleBack = (e) => {
    e.preventDefault();
    
    window.close();
    
    setTimeout(() => {
      try { if (!window.closed) nav(`/assignment/${assignmentId}`); } catch {}
    }, 50);
  };

  if (loading) {
    return (
      <Layout>
        <div className="text-neutral-500">Загрузка…</div>
      </Layout>
    );
  }

  if (!res) {
    return (
      <Layout>
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-2">
            <a href={`/assignment/${assignmentId}`} onClick={handleBack} className="text-brand-600 hover:underline">
              <ArrowLeft size={16} /> назад к решению
            </a>
          </div>
          <div className="flex items-center gap-2">
            <Button onClick={() => nav(0)} variant="outline">
              <RotateCcw size={16} className="mr-1" /> Обновить
            </Button>
          </div>
        </div>
        <Card>
          <div className="text-neutral-500 p-3">Нет данных для отображения.</div>
        </Card>
      </Layout>
    );
  }

  const cases = res.cases ?? res.testCases ?? res.results ?? res.result?.cases ?? res.result?.results ?? [];
  const status = String(res.status || res.verdict || '').toLowerCase();
  const pending = res.isPending === true || res.result?.pending === true || ['preparing', 'queued', 'running', 'pending'].includes(status);
  const message = res.message || res.result?.message || '';
  const score = res.score ?? res.Score ?? res.result?.score;
  const stdout = res.stdout || res.result?.stdout || '';
  const stderr = res.stderr || res.compileError || res.result?.stderr || res.result?.compileStderr || '';
  const passedAll =
    (res.passedAll === true) ||
    (res.passedAllTests === true) ||
    status === 'accepted' ||
    (Array.isArray(cases) && cases.length > 0 && cases.every(c => c?.passed === true || c?.status === 'OK' || c?.status === 'ok'));

  return (
    <Layout>
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <a href={`/assignment/${assignmentId}`} onClick={handleBack} className="text-brand-600 hover:underline">
            <ArrowLeft size={16} /> назад к решению
          </a>
          {view === 'smoke' && <Badge>Пробный прогон</Badge>}
        </div>
        <div className="flex items-center gap-2">
          <Button onClick={() => nav(0)} variant="outline">
            <RotateCcw size={16} className="mr-1" /> Обновить
          </Button>
        </div>
      </div>

      <Card>
        <div className="p-4">
          <div className="mb-3">
            {pending ? (
              <div className="text-sky-700 font-medium">Проверка ещё выполняется</div>
            ) : passedAll ? (
              <div className="text-emerald-700 font-medium">Все тесты пройдены</div>
            ) : status === 'compileerror' ? (
              <div className="text-red-700 font-medium">Ошибка компиляции</div>
            ) : status === 'notestsconfigured' ? (
              <div className="text-red-700 font-medium">Для задания не настроены тесты</div>
            ) : status === 'judgeunavailable' ? (
              <div className="text-red-700 font-medium">Система проверки временно недоступна</div>
            ) : status === 'policyfailed' ? (
              <div className="text-red-700 font-medium">Решение заблокировано анализатором кода</div>
            ) : status === 'languagenotallowed' ? (
              <div className="text-red-700 font-medium">Этот язык не разрешён для задания</div>
            ) : (
              <div className="text-red-700 font-medium">Не все тесты пройдены</div>
            )}
          </div>

          {(message || score != null || stdout || stderr) ? (
            <div className="mb-4 rounded-xl border border-neutral-200 dark:border-neutral-800/70 bg-neutral-50/70 dark:bg-neutral-900/40 p-3 text-sm space-y-2">
              {score != null ? <div><span className="font-medium">Score:</span> {score}</div> : null}
              {message ? <div className="text-neutral-700 dark:text-neutral-300">{displayClean(message)}</div> : null}
              {stdout ? <pre className="whitespace-pre-wrap text-xs">stdout
{displayClean(stdout)}</pre> : null}
              {stderr ? <pre className="whitespace-pre-wrap text-xs text-red-600">stderr
{displayClean(stderr)}</pre> : null}
            </div>
          ) : null}

          <div className="space-y-4">
            {Array.isArray(cases) && cases.length > 0 ? cases.map((c, i) => {
              const expectedText = c.expected ?? c.expectedOutput ?? c.ExpectedOutput ?? '';
              const actualText   = c.actual   ?? c.actualOutput   ?? c.ActualOutput   ?? '';
              return (
                <div key={i} className="rounded border p-3">
                  <div className="flex items-center justify-between mb-2">
                    <div className="text-sm font-medium">Тест #{i + 1}</div>
                    <div className={`text-xs px-2 py-0.5 rounded ${c.passed || c.status === 'OK' || c.status === 'ok' ? 'bg-emerald-100 text-emerald-700' : 'bg-red-100 text-red-700'}`}>
                      {c.passed || c.status === 'OK' || c.status === 'ok' ? 'OK' : 'FAIL'}
                    </div>
                  </div>

                  {'input' in c || 'Input' in c ? (
                    <>
                      <div className="text-xs text-neutral-500 mb-1">Ввод</div>
                      <pre className="whitespace-pre-wrap text-sm">{displayClean(c.input ?? c.Input)}</pre>
                    </>
                  ) : null}

                  {(expectedText ?? '') !== '' && (
                    <>
                      <div className="text-xs text-neutral-500 mt-2 mb-1">Ожидаемый вывод</div>
                      <pre className="whitespace-pre-wrap text-sm">{displayClean(expectedText)}</pre>
                    </>
                  )}

                  {(actualText ?? '') !== '' && (
                    <>
                      <div className="text-xs text-neutral-500 mt-2 mb-1">Фактически</div>
                      <pre className="whitespace-pre-wrap text-sm">{displayClean(actualText)}</pre>
                    </>
                  )}

                  {(c.compileStderr || c.stderr || c.error) && (
                    <div className="mt-2">
                      <div className="text-xs text-neutral-500 mb-1">Ошибки</div>
                      {(() => {
                        const rawErr = c.compileStderr || c.stderr || c.error;
                        const p = parsePolicyText(rawErr);
                        if (!p) {
                          return (
                            <pre className="whitespace-pre-wrap text-xs text-red-600">
                              {displayClean(rawErr)}
                            </pre>
                          );
                        }

                        return (
                          <div className="text-xs text-red-600 space-y-2">
                            <div className="font-medium">{p.title}</div>
                            {p.bullets.length > 0 && (
                              <ul className="list-disc pl-5 space-y-1">
                                {p.bullets.map((b, idx) => (
                                  <li key={idx}>{b}</li>
                                ))}
                              </ul>
                            )}
                            {p.hitLines.length > 0 && (
                              <div className="text-[11px] text-red-500 whitespace-pre-wrap">
                                {p.hitLines.join('\n')}
                              </div>
                            )}
                            
                            
                          </div>
                        );
                      })()}
                    </div>
                  )}
                </div>
              );
            }) : !pending ? (
              <div className="rounded border p-3 text-sm text-neutral-500">Детальных тест-кейсов в ответе нет. Итоговый статус показан выше.</div>
            ) : (
              <div className="rounded border p-3 text-sm text-neutral-500">Жду результат проверки…</div>
            )}
          </div>
        </div>
      </Card>
    </Layout>
  );
}
