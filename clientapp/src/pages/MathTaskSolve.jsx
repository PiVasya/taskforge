import React, { useEffect, useMemo, useState } from 'react';
import { Card, Button, Field, Input, Textarea, Select, Badge } from '../components/ui';
import { startMathTask, submitMathTask } from '../api/mathTasks';
import { useNotify } from '../components/notify/NotifyProvider';
import StatementViewer from '../components/tiptap/StatementViewer';

function fmtSeconds(total) {
  if (total == null) return '';
  const t = Math.max(0, Math.floor(total));
  const m = Math.floor(t / 60);
  const s = t % 60;
  return `${m}:${String(s).padStart(2, '0')}`;
}

export default function MathTaskSolve({ assignmentId, assignment }) {
  const notify = useNotify();
  const [loading, setLoading] = useState(false);
  const [startData, setStartData] = useState(null);
  const [answers, setAnswers] = useState({});
  const [submitLoading, setSubmitLoading] = useState(false);
  const [result, setResult] = useState(null);
  const [limitReached, setLimitReached] = useState(false);

  const timeLimit = startData?.timeLimitSeconds ?? null;
  const startedAt = startData?.startedAt ? new Date(startData.startedAt) : null;
  const [nowTick, setNowTick] = useState(Date.now());
  useEffect(() => {
    if (!startedAt || !timeLimit) return;
    const id = setInterval(() => setNowTick(Date.now()), 500);
    return () => clearInterval(id);
  }, [startedAt, timeLimit]);

  const secondsLeft = useMemo(() => {
    if (!startedAt || !timeLimit) return null;
    const elapsed = (nowTick - startedAt.getTime()) / 1000;
    return Math.max(0, Math.ceil(timeLimit - elapsed));
  }, [startedAt, timeLimit, nowTick]);

  const begin = async () => {
    try {
      setLoading(true);
      setLimitReached(false);
      setStartData(null);
      setResult(null);
      setAnswers({});
      const data = await startMathTask(assignmentId);
      setStartData(data);
    } catch (err) {
      if (err?.response?.status === 409) setLimitReached(true);
      notify.error(err?.userMessage || err?.message || 'Не удалось начать math-задание');
    } finally {
      setLoading(false);
    }
  };

  const doSubmit = async () => {
    if (!startData?.attemptId) return;
    try {
      setSubmitLoading(true);
      const payload = {
        attemptId: startData.attemptId,
        answers: Object.entries(answers).map(([blockId, v]) => ({
          blockId,
          text: v?.text ?? null,
          selectedOptionKeys: v?.selectedOptionKeys ?? null,
          orderedItems: v?.orderedItems ?? null,
          matchPairs: v?.matchPairs ?? null,
        })),
      };
      const res = await submitMathTask(assignmentId, payload);
      setResult(res);
      if (Number.isFinite(startData?.attemptNumber) && Number.isFinite(startData?.maxAttempts) && startData.attemptNumber >= startData.maxAttempts) {
        setLimitReached(true);
      }
      notify.success(res.passed ? 'Математическое задание засчитано ✅' : 'Попытка завершена');
    } catch (err) {
      notify.error(err?.userMessage || err?.message || 'Не удалось отправить ответы');
    } finally {
      setSubmitLoading(false);
    }
  };

  useEffect(() => {
    if (!startData?.attemptId || secondsLeft == null || secondsLeft > 0 || submitLoading || result) return;
    doSubmit();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [secondsLeft]);

  const blocks = startData?.blocks ?? [];

  const moveOrderItem = (blockId, idx, dir) => {
    setAnswers((prev) => {
      const cur = prev[blockId]?.orderedItems ?? [];
      const j = idx + dir;
      if (j < 0 || j >= cur.length) return prev;
      const next = [...cur];
      [next[idx], next[j]] = [next[j], next[idx]];
      return { ...prev, [blockId]: { ...(prev[blockId] || {}), orderedItems: next } };
    });
  };

  const renderBlockBody = (block) => {
    const kind = String(block.kind || '').toLowerCase();

    if (kind === 'info') {
      return <div className="text-sm text-neutral-500">Это информационный блок. Он не оценивается, но помогает провести решение по шагам.</div>;
    }

    if (kind === 'single-choice' || kind === 'multi-choice') {
      const selected = answers[block.id]?.selectedOptionKeys ?? [];
      const isMulti = kind === 'multi-choice';
      return (
        <div className="space-y-2">
          {(block.options || []).map((o) => (
            <label key={o.key} className="flex items-center gap-2 text-sm cursor-pointer">
              <input
                type={isMulti ? 'checkbox' : 'radio'}
                name={`math-${block.id}`}
                checked={selected.includes(o.key)}
                onChange={() => setAnswers((prev) => {
                  const cur = prev[block.id]?.selectedOptionKeys ?? [];
                  if (!isMulti) return { ...prev, [block.id]: { selectedOptionKeys: [o.key] } };
                  const set = new Set(cur);
                  set.has(o.key) ? set.delete(o.key) : set.add(o.key);
                  return { ...prev, [block.id]: { selectedOptionKeys: Array.from(set) } };
                })}
              />
              <span>{o.text}</span>
            </label>
          ))}
        </div>
      );
    }

    if (kind === 'number' || kind === 'expression' || kind === 'set') {
      return (
        <Field label={kind === 'number' ? 'Ответ' : kind === 'set' ? 'Множество / список' : 'Формула / выражение'}>
          <Textarea
            rows={kind === 'expression' ? 3 : 2}
            value={answers[block.id]?.text || ''}
            onChange={(e) => setAnswers((prev) => ({ ...prev, [block.id]: { ...(prev[block.id] || {}), text: e.target.value } }))}
            placeholder={kind === 'number' ? 'Например: 3.14' : kind === 'set' ? 'Например: 1, 2, 3' : 'Например: (x-1)(x+1)'}
          />
        </Field>
      );
    }

    if (kind === 'order') {
      const cur = answers[block.id]?.orderedItems?.length ? answers[block.id].orderedItems : (block.orderItems || []);
      return (
        <div className="space-y-2">
          {cur.map((item, idx) => (
            <div key={`${item}_${idx}`} className="flex items-center gap-2 rounded-xl border border-neutral-200 dark:border-neutral-800 px-3 py-2">
              <Badge>{idx + 1}</Badge>
              <div className="flex-1">{item}</div>
              <Button variant="outline" onClick={() => moveOrderItem(block.id, idx, -1)}>↑</Button>
              <Button variant="outline" onClick={() => moveOrderItem(block.id, idx, 1)}>↓</Button>
            </div>
          ))}
        </div>
      );
    }

    if (kind === 'match') {
      const pairs = answers[block.id]?.matchPairs ?? (block.matchLeftItems || []).map((x) => ({ leftKey: x.key, rightKey: '' }));
      return (
        <div className="space-y-3">
          {(block.matchLeftItems || []).map((left) => (
            <div key={left.key} className="grid md:grid-cols-[1fr_220px] gap-3 items-center">
              <div className="rounded-xl border border-neutral-200 dark:border-neutral-800 px-3 py-2">{left.text}</div>
              <Select
                value={pairs.find((x) => x.leftKey === left.key)?.rightKey || ''}
                onChange={(e) => setAnswers((prev) => {
                  const cur = prev[block.id]?.matchPairs ?? (block.matchLeftItems || []).map((x) => ({ leftKey: x.key, rightKey: '' }));
                  const next = cur.map((x) => x.leftKey === left.key ? { ...x, rightKey: e.target.value } : x);
                  return { ...prev, [block.id]: { ...(prev[block.id] || {}), matchPairs: next } };
                })}
              >
                <option value="">— выбери —</option>
                {(block.matchRightItems || []).map((right) => <option key={right.key} value={right.key}>{right.text}</option>)}
              </Select>
            </div>
          ))}
        </div>
      );
    }

    return <div className="text-sm text-rose-500">Неизвестный тип блока: {kind}</div>;
  };

  return (
    <div className="max-w-5xl mx-auto space-y-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold">{assignment?.title || 'Математика'}</h1>
          {assignment?.description && (
            <div className="mt-2 text-sm text-neutral-600 dark:text-neutral-400">
              <StatementViewer value={assignment.description} />
            </div>
          )}
        </div>
        <div className="text-right">
          {startData && <div className="text-sm text-neutral-600 dark:text-neutral-400">Попытка: <b>{startData.attemptNumber}</b> / {startData.maxAttempts}</div>}
          {timeLimit ? <div className="mt-1"><Badge variant={secondsLeft !== null && secondsLeft <= 10 ? 'destructive' : 'secondary'}>Таймер: {fmtSeconds(secondsLeft)}</Badge></div> : null}
        </div>
      </div>

      {!startData && (
        <Card>
          <div className="space-y-3">
            <div className="text-sm text-neutral-600 dark:text-neutral-400">Здесь можно строить решения по блокам: формулы, числа, множества, шаги, соответствия и тестовые подпункты.</div>
            <div className="flex gap-3">
              <Button onClick={begin} disabled={loading || limitReached}>{limitReached ? 'Лимит попыток' : (loading ? 'Запуск…' : 'Начать задание')}</Button>
            </div>
            {limitReached ? <div className="text-sm text-rose-600">Достигнут лимит попыток.</div> : null}
          </div>
        </Card>
      )}

      {result && (
        <Card>
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <div className="text-lg font-semibold">Результат: {result.scorePercent}% ({result.earnedScore}/{result.totalScore} баллов)</div>
              <div className="text-sm text-neutral-600 dark:text-neutral-400">Порог: {result.passPercent}%. {result.timeExpired ? '⏱️ Время вышло.' : (result.passed ? '✅ Засчитано.' : '❌ Не засчитано.')}</div>
            </div>
            <div className="flex gap-3">
              <Button variant="outline" onClick={() => { setStartData(null); setResult(null); }}>Закрыть</Button>
              {startData && startData.attemptNumber < startData.maxAttempts ? (
                <Button onClick={begin} disabled={loading}>{loading ? 'Запуск…' : 'Новая попытка'}</Button>
              ) : <Button variant="secondary" disabled>Лимит попыток</Button>}
            </div>
          </div>
        </Card>
      )}

      {startData && !result && (
        <div className="space-y-4">
          {blocks.map((block, idx) => (
            <Card key={block.id}>
              <div className="space-y-4">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <div className="font-medium">{idx + 1}. {block.prompt || 'Блок'}</div>
                    {block.promptContentJson ? <div className="mt-2"><StatementViewer value={block.promptContentJson} /></div> : null}
                  </div>
                  <div className="flex items-center gap-2">
                    <Badge variant="outline">{block.kind}</Badge>
                    {block.score > 0 ? <Badge>{block.score} б.</Badge> : null}
                  </div>
                </div>
                {renderBlockBody(block)}
              </div>
            </Card>
          ))}

          <div className="flex justify-end gap-3">
            <Button variant="outline" onClick={() => { setStartData(null); setAnswers({}); }}>Отмена</Button>
            <Button onClick={doSubmit} disabled={submitLoading}>{submitLoading ? 'Отправка…' : 'Отправить решение'}</Button>
          </div>
        </div>
      )}
    </div>
  );
}
