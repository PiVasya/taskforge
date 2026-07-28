import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Card, Button } from '../components/ui';
import { startMathTask, submitMathTask } from '../api/mathTasks';
import { useNotify } from '../components/notify/NotifyProvider';
import StatementViewer from '../components/tiptap/StatementViewer';
import AttemptCountdown from '../features/attempts/AttemptCountdown';
import {
  destroyAttemptAnswers,
  getAttemptAnswers,
  resetAttemptAnswers,
  subscribeAttemptAnswers,
} from '../features/attempts/attemptAnswerStore';
import MathTaskBlock from '../features/math-task/MathTaskBlock';

function hashActivityText(value) {
  const text = typeof value === 'string' ? value : '';
  let hash = 2166136261;
  for (let index = 0; index < text.length; index += 1) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return `fnv1a:${(hash >>> 0).toString(16).padStart(8, '0')}:${text.length}`;
}

function summarizeAnswerDraft(answers) {
  const entries = Object.entries(answers || {});
  const textParts = [];
  let selectedCount = 0;
  let orderedCount = 0;
  let pairCount = 0;
  entries.forEach(([, value]) => {
    const text = typeof value?.text === 'string' ? value.text : '';
    if (text) textParts.push(text);
    if (Array.isArray(value?.selectedOptionKeys)) selectedCount += value.selectedOptionKeys.length;
    if (Array.isArray(value?.orderedItems)) orderedCount += value.orderedItems.length;
    if (Array.isArray(value?.matchPairs)) pairCount += value.matchPairs.filter((item) => item?.rightKey).length;
  });
  const joinedText = textParts.join('\n');
  return {
    touched: entries.length,
    selectedCount,
    orderedCount,
    pairCount,
    textLength: joinedText.length,
    textHash: hashActivityText(joinedText),
    textSample: joinedText.slice(0, 1200),
  };
}

function MathTaskSolve({ assignmentId, assignment, onActivity }) {
  const notify = useNotify();
  const [loading, setLoading] = useState(false);
  const [startData, setStartData] = useState(null);
  const [submitLoading, setSubmitLoading] = useState(false);
  const [result, setResult] = useState(null);
  const [limitReached, setLimitReached] = useState(false);
  const lastAnswerActivityRef = useRef({ signature: '', at: 0 });

  const storeKey = startData?.attemptId ? `math:${startData.attemptId}` : '';
  const blocks = useMemo(() => startData?.blocks ?? [], [startData?.blocks]);

  useEffect(() => () => {
    if (storeKey) destroyAttemptAnswers(storeKey);
  }, [storeKey]);

  const begin = useCallback(async () => {
    try {
      setLoading(true);
      setLimitReached(false);
      if (storeKey) destroyAttemptAnswers(storeKey);
      setStartData(null);
      setResult(null);
      onActivity?.('math_started', { payload: { kind: 'math' } });
      const data = await startMathTask(assignmentId);
      resetAttemptAnswers(`math:${data.attemptId}`);
      lastAnswerActivityRef.current = { signature: '', at: 0 };
      setStartData(data);
    } catch (error) {
      if (error?.response?.status === 409) setLimitReached(true);
      notify.error(error?.userMessage || error?.message || 'Не удалось начать math-задание');
    } finally {
      setLoading(false);
    }
  }, [assignmentId, notify, onActivity, storeKey]);

  const doSubmit = useCallback(async () => {
    if (!startData?.attemptId || !storeKey || submitLoading || result) return;
    const answers = getAttemptAnswers(storeKey);
    try {
      setSubmitLoading(true);
      const summary = summarizeAnswerDraft(answers);
      onActivity?.('math_answers_final', {
        attemptId: startData.attemptId,
        textLength: summary.textLength,
        textHash: summary.textHash,
        textSample: summary.textSample,
        payload: {
          kind: 'math',
          touched: summary.touched,
          selectedCount: summary.selectedCount,
          orderedCount: summary.orderedCount,
          pairCount: summary.pairCount,
        },
      });
      const payload = {
        attemptId: startData.attemptId,
        answers: Object.entries(answers).map(([blockId, value]) => ({
          blockId,
          text: value?.text ?? null,
          selectedOptionKeys: value?.selectedOptionKeys ?? null,
          orderedItems: value?.orderedItems ?? null,
          matchPairs: value?.matchPairs ?? null,
        })),
      };
      const response = await submitMathTask(assignmentId, payload);
      onActivity?.('math_finished', {
        attemptId: startData.attemptId,
        payload: { passed: Boolean(response?.passed), scorePercent: response?.scorePercent ?? null },
      });
      destroyAttemptAnswers(storeKey);
      setResult(response);
      if (
        Number.isFinite(startData?.attemptNumber)
        && Number.isFinite(startData?.maxAttempts)
        && startData.attemptNumber >= startData.maxAttempts
      ) {
        setLimitReached(true);
      }
      notify.success(response.passed ? 'Математическое задание засчитано ✅' : 'Попытка завершена');
    } catch (error) {
      onActivity?.('submit_failed', {
        attemptId: startData?.attemptId || null,
        payload: { kind: 'math', message: error?.userMessage || error?.message || 'Не удалось отправить ответы' },
      });
      notify.error(error?.userMessage || error?.message || 'Не удалось отправить ответы');
    } finally {
      setSubmitLoading(false);
    }
  }, [assignmentId, notify, onActivity, result, startData, storeKey, submitLoading]);

  useEffect(() => {
    if (!storeKey || result) return undefined;
    return subscribeAttemptAnswers(storeKey, () => {
      const answers = getAttemptAnswers(storeKey);
      const signature = JSON.stringify(answers);
      if (signature === lastAnswerActivityRef.current.signature) return;
      const now = Date.now();
      if (lastAnswerActivityRef.current.signature && now - lastAnswerActivityRef.current.at < 2500) return;
      lastAnswerActivityRef.current = { signature, at: now };
      const summary = summarizeAnswerDraft(answers);
      if (summary.touched <= 0) return;
      onActivity?.('math_answers_changed', {
        attemptId: startData?.attemptId,
        textLength: summary.textLength,
        textHash: summary.textHash,
        textSample: summary.textSample,
        payload: {
          kind: 'math',
          touched: summary.touched,
          selectedCount: summary.selectedCount,
          orderedCount: summary.orderedCount,
          pairCount: summary.pairCount,
        },
      });
    });
  }, [onActivity, result, startData?.attemptId, storeKey]);

  const closeAttempt = useCallback(() => {
    if (storeKey) destroyAttemptAnswers(storeKey);
    setStartData(null);
    setResult(null);
  }, [storeKey]);

  return (
    <div className="max-w-5xl mx-auto space-y-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold">{assignment?.title || 'Математика'}</h1>
          {assignment?.description ? (
            <div className="mt-2 text-sm text-neutral-600 dark:text-neutral-400">
              <StatementViewer value={assignment.description} />
            </div>
          ) : null}
        </div>
        <div className="text-right">
          {startData ? (
            <div className="text-sm text-neutral-600 dark:text-neutral-400">
              Попытка: <b>{startData.attemptNumber}</b> / {startData.maxAttempts}
            </div>
          ) : null}
          {startData?.timeLimitSeconds ? (
            <div className="mt-1">
              <AttemptCountdown
                startedAt={startData.startedAt}
                timeLimitSeconds={startData.timeLimitSeconds}
                onExpire={doSubmit}
                disabled={submitLoading || Boolean(result)}
              />
            </div>
          ) : null}
        </div>
      </div>

      {!startData ? (
        <Card>
          <div className="space-y-3">
            <div className="text-sm text-neutral-600 dark:text-neutral-400">
              Здесь можно строить решения по блокам: формулы, числа, множества, шаги, соответствия и тестовые подпункты.
            </div>
            <div className="flex gap-3">
              <Button onClick={begin} disabled={loading || limitReached}>
                {limitReached ? 'Лимит попыток' : (loading ? 'Запуск…' : 'Начать задание')}
              </Button>
            </div>
            {limitReached ? <div className="text-sm text-rose-600">Достигнут лимит попыток.</div> : null}
          </div>
        </Card>
      ) : null}

      {result ? (
        <Card>
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <div className="text-lg font-semibold">
                Результат: {result.scorePercent}% ({result.earnedScore}/{result.totalScore} баллов)
              </div>
              <div className="text-sm text-neutral-600 dark:text-neutral-400">
                Порог: {result.passPercent}%. {result.timeExpired ? '⏱️ Время вышло.' : (result.passed ? '✅ Засчитано.' : '❌ Не засчитано.')}
              </div>
            </div>
            <div className="flex gap-3">
              <Button variant="outline" onClick={closeAttempt}>Закрыть</Button>
              {startData && startData.attemptNumber < startData.maxAttempts ? (
                <Button onClick={begin} disabled={loading}>{loading ? 'Запуск…' : 'Новая попытка'}</Button>
              ) : (
                <Button variant="secondary" disabled>Лимит попыток</Button>
              )}
            </div>
          </div>
        </Card>
      ) : null}

      {startData && !result ? (
        <div className="space-y-4">
          {blocks.map((block, index) => (
            <MathTaskBlock key={block.id} storeKey={storeKey} block={block} index={index} />
          ))}
          <div className="flex justify-end gap-3">
            <Button variant="outline" onClick={closeAttempt} disabled={submitLoading}>Отмена</Button>
            <Button onClick={doSubmit} disabled={submitLoading}>{submitLoading ? 'Отправка…' : 'Отправить решение'}</Button>
          </div>
        </div>
      ) : null}
    </div>
  );
}

export default React.memo(MathTaskSolve);
