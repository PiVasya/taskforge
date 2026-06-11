import React, { useEffect, useState } from 'react';
import { Link, useParams, useNavigate, useSearchParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card, Button, Badge } from '../components/ui';
import { getAssignment } from '../api/assignments';
import { getMySolutionDetails } from '../api/solutions';
import { sanitizeRunnerText } from '../utils/runnerText';
import { useRoleFlags } from '../contexts/EditorModeContext';
import { ArrowLeft, RotateCcw } from 'lucide-react';

function displayClean(s) {
  if (s == null) return '';
  return String(s);
}

function displayRunnerClean(s) {
  return sanitizeRunnerText(s);
}

function isCasePassedStrict(c) {
  if (!c || typeof c !== 'object') return false;
  if (typeof c.passed === 'boolean') return c.passed;
  if (typeof c.Passed === 'boolean') return c.Passed;
  const status = String(c.status ?? c.Status ?? '').trim().toLowerCase();
  return status === 'accepted' || status === 'passed' || status === 'success';
}

function isHiddenTestCase(t) {
  return t?.isHidden === true || t?.hidden === true || t?.Hidden === true;
}

function isPlatformPolicyPattern(patternId) {
  const id = String(patternId || '').trim().toLowerCase();
  if (!id || id === '-') return false;
  return (
    id === 'platform.security' ||
    id.startsWith('py.') ||
    id.startsWith('js.') ||
    id.startsWith('c.') ||
    id.startsWith('cpp.') ||
    id.startsWith('cs.') ||
    id.startsWith('java.') ||
    id.startsWith('pas.')
  );
}

function cleanupPolicyMessage(message) {
  return String(message || '')
    .replace(/\(pattern_id=[^)]+\)/gi, '')
    .replace(/\s{2,}/g, ' ')
    .trim();
}

function policyValue(obj, ...names) {
  if (!obj || typeof obj !== 'object') return '';
  for (const name of names) {
    if (obj[name] != null) return obj[name];
  }
  return '';
}

function buildPolicyUiFromParts(violations, hits, raw = '') {
  const taskViolations = violations.filter(v => !isPlatformPolicyPattern(v.patternId));
  const platformViolations = violations.filter(v => isPlatformPolicyPattern(v.patternId));
  const taskHits = hits.filter(h => !isPlatformPolicyPattern(h.patternId));

  if (taskViolations.length === 0 && platformViolations.length > 0) {
    return {
      kind: 'platform',
      title: 'Решение отклонено системой безопасности',
      bullets: ['Код использует системные возможности, которые нельзя запускать в песочнице.'],
      hitLines: [],
      raw,
    };
  }

  const forbidden = [];
  const required = [];
  const cyrillic = [];
  const other = [];

  for (const v of taskViolations) {
    const code = String(v.code || '').toLowerCase();
    const patternId = String(v.patternId || '').toLowerCase();
    const msg = cleanupPolicyMessage(v.message || '');
    if (!msg) continue;
    if (code.includes('cyrillic') || patternId === 'unicode.cyrillic_in_code' || /кириллиц/i.test(msg)) {
      cyrillic.push(msg);
    } else if (code.includes('missing_required') || /обязатель|не найдено обязательное/i.test(msg)) {
      required.push(msg.replace(/^Не найдено обязательное:\s*/i, ''));
    } else if (code.includes('forbidden') || /запрещ/i.test(msg)) {
      forbidden.push(msg.replace(/^Запрещено:\s*/i, '').replace(/^Запрещённая конструкция:\s*/i, ''));
    } else {
      other.push(msg);
    }
  }

  const unique = (items) => [...new Set(items.filter(Boolean))];
  const bullets = [];
  if (cyrillic.length) bullets.push(...unique(cyrillic));
  if (forbidden.length) bullets.push(`Запрещено по условию задания: ${unique(forbidden).join(' • ')}`);
  if (required.length) bullets.push(`Нужно обязательно использовать: ${unique(required).join(' • ')}`);
  if (other.length) bullets.push(...unique(other));
  if (platformViolations.length) {
    bullets.push('Дополнительно решение отклонено системой безопасности. Подробности системного ограничения скрыты.');
  }

  const hitLines = taskHits
    .filter(h => h.needle || h.pos || h.preview)
    .slice(0, 4)
    .map(h => {
      const parts = [];
      if (h.needle) parts.push(`«${h.needle}»`);
      if (h.pos !== '' && h.pos != null) parts.push(`позиция ${h.pos}`);
      if (h.preview) parts.push(`фрагмент: ${h.preview}`);
      return `• Найдено ${parts.join(', ')}`;
    });

  return {
    kind: taskViolations.length ? (platformViolations.length ? 'mixed' : 'task') : 'mixed',
    title: cyrillic.length && taskViolations.length === cyrillic.length && !platformViolations.length
      ? 'Решение отклонено: в коде найдена кириллица'
      : 'Решение не принято: анализатор кода нашёл нарушение',
    bullets: bullets.length ? bullets : ['Анализатор кода нашёл нарушение правил задания.'],
    hitLines,
    raw,
  };
}

function parsePolicyObject(payload) {
  if (!payload || typeof payload !== 'object') return null;
  const errors = Array.isArray(payload.errors) ? payload.errors : [];
  const hits = Array.isArray(payload.hits) ? payload.hits : [];
  if (!errors.length && !hits.length && payload.policyKind !== 'platform') return null;

  const violations = errors.map(e => ({
    code: policyValue(e, 'code'),
    message: cleanupPolicyMessage(policyValue(e, 'message')),
    patternId: policyValue(e, 'pattern_id', 'patternId'),
  })).filter(v => v.code || v.message || v.patternId);

  const parsedHits = hits.map(h => ({
    patternId: policyValue(h, 'pattern_id', 'patternId'),
    needle: policyValue(h, 'needle'),
    pos: policyValue(h, 'position', 'pos'),
    preview: policyValue(h, 'preview'),
  })).filter(h => h.patternId || h.needle || h.preview);

  if (violations.length === 0 && payload.policyKind === 'platform') {
    violations.push({ code: 'sandbox_security', message: 'Код использует системные возможности, которые нельзя запускать в песочнице.', patternId: 'platform.security' });
  }

  return buildPolicyUiFromParts(violations, parsedHits, payload);
}

function parsePolicyText(raw) {
  if (raw && typeof raw === 'object') return parsePolicyObject(raw);

  const txt = String(raw || '');
  if (!txt) return null;
  const isPolicy = txt.includes('[policy_failed]') || txt.toLowerCase().includes('code analyzer blocked');
  if (!isPolicy) return null;

  const lines = txt.split('\n').map(s => s.trim()).filter(Boolean);
  const violations = [];
  const hits = [];
  let inHits = false;

  for (const l of lines) {
    if (l.startsWith('[hits]')) { inHits = true; continue; }
    if (l.startsWith('[') && l.endsWith(']')) { inHits = false; continue; }
    if (!l.startsWith('- ')) continue;
    const body = l.replace(/^\-\s*/, '');
    if (inHits) {
      const mId = body.match(/\bid=([^\s]+)\b/i);
      const mNeedle = body.match(/needle='([^']*)'/i);
      const mPos = body.match(/pos=(\d+)/i);
      const mPrev = body.match(/preview='([^']*)'/i);
      hits.push({
        patternId: mId?.[1] || '',
        needle: mNeedle?.[1] || '',
        pos: mPos?.[1] || '',
        preview: mPrev?.[1] || '',
      });
      continue;
    }

    const match = body.match(/^([^:]+):\s*(.*?)(?:\s*\(pattern_id=([^)]*)\))?$/i);
    if (match) {
      violations.push({
        code: match[1] || '',
        message: cleanupPolicyMessage(match[2] || body),
        patternId: match[3] || '',
      });
    } else {
      violations.push({ code: '', message: cleanupPolicyMessage(body), patternId: '' });
    }
  }

  return buildPolicyUiFromParts(violations, hits, txt);
}

function extractPolicyPayload(resObj) {
  const candidates = [
    resObj?.policyDetails,
    resObj?.policyError,
    resObj?.result?.policyDetails,
    resObj?.result?.policyError,
    resObj?.result?.raw,
    resObj?.raw,
  ];
  for (const candidate of candidates) {
    if (!candidate) continue;
    if (typeof candidate === 'string' || typeof candidate === 'object') return candidate;
  }
  return '';
}

export default function AssignmentResultsPage() {
  const { assignmentId } = useParams();
  const nav = useNavigate();
  const [searchParams] = useSearchParams();
  const view = searchParams.get('view') || 'full';
  const solutionId = searchParams.get('solutionId') || null;
  const { isAdmin } = useRoleFlags();

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

  const rawCases = res.cases ?? res.testCases ?? res.results ?? res.result?.cases ?? res.result?.results ?? [];
  const canViewHiddenTests = isAdmin || a?.canEdit === true;
  const cases = Array.isArray(rawCases) ? rawCases.filter((c) => canViewHiddenTests || !isHiddenTestCase(c)) : [];
  const status = String(res.status || res.verdict || '').toLowerCase();
  const pending = res.isPending === true || res.result?.pending === true || ['preparing', 'queued', 'running', 'pending'].includes(status);
  const message = res.message || res.result?.message || '';
  const stdout = res.stdout || res.result?.stdout || '';
  const stderr = res.stderr || res.compileError || res.result?.stderr || res.result?.compileStderr || '';
  const policyUi = parsePolicyText(extractPolicyPayload(res));
  const explicitFailed = res.passedAll === false || res.passedAllTests === false || res.PassedAll === false || res.PassedAllTests === false;
  const passedAll = !explicitFailed && (
    res.passedAll === true ||
    res.passedAllTests === true ||
    status === 'accepted' ||
    (Array.isArray(cases) && cases.length > 0 && cases.every(isCasePassedStrict))
  );

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

          {policyUi ? (
            <div className="mb-4 rounded-xl border border-red-200 bg-red-50/80 p-4 text-sm text-red-800 dark:border-red-900/60 dark:bg-red-950/30 dark:text-red-100">
              <div className="font-semibold mb-2">{policyUi.title}</div>
              {policyUi.bullets?.length ? (
                <ul className="list-disc pl-5 space-y-1">
                  {policyUi.bullets.slice(0, 8).map((b, idx) => <li key={idx}>{b}</li>)}
                </ul>
              ) : null}
              {policyUi.hitLines?.length ? (
                <div className="mt-3 rounded-lg bg-white/50 p-3 text-xs whitespace-pre-wrap dark:bg-black/20">
                  {policyUi.hitLines.join('\n')}
                </div>
              ) : null}
            </div>
          ) : null}

          {(message || stdout || stderr) ? (
            <div className="mb-4 rounded-xl border border-neutral-200 dark:border-neutral-800/70 bg-neutral-50/70 dark:bg-neutral-900/40 p-3 text-sm space-y-2">
              {message && !policyUi ? <div className="text-neutral-700 dark:text-neutral-300">{displayRunnerClean(message)}</div> : null}
              {stdout ? <pre className="whitespace-pre-wrap text-xs">stdout
{displayClean(stdout)}</pre> : null}
              {stderr && !policyUi ? <pre className="whitespace-pre-wrap text-xs text-red-600">stderr
{displayRunnerClean(stderr)}</pre> : null}
            </div>
          ) : null}

          <div className="space-y-4">
            {Array.isArray(cases) && cases.length > 0 ? cases.map((c, i) => {
              const expectedText = c.expected ?? c.expectedOutput ?? c.ExpectedOutput ?? '';
              const actualText   = c.actual   ?? c.actualOutput   ?? c.ActualOutput   ?? '';
              return (
                <div key={i} className={`rounded border p-3 ${isHiddenTestCase(c) ? 'border-amber-300/60 bg-amber-500/5' : ''}`}>
                  <div className="flex items-center justify-between gap-2 mb-2">
                    <div className="flex items-center gap-2">
                      <div className="text-sm font-medium">Тест #{i + 1}</div>
                      {isHiddenTestCase(c) && <Badge intent="warning">Скрытый тест</Badge>}
                    </div>
                    <div className={`text-xs px-2 py-0.5 rounded ${isCasePassedStrict(c) ? 'bg-emerald-100 text-emerald-700' : 'bg-red-100 text-red-700'}`}>
                      {isCasePassedStrict(c) ? 'OK' : 'FAIL'}
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
                              {displayRunnerClean(rawErr)}
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
