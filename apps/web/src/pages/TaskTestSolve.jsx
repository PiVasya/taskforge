import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Card, Button } from '../components/ui';
import { startTaskTest, submitTaskTest } from '../api/taskTests';
import { useNotify } from '../components/notify/NotifyProvider';
import StatementViewer from '../components/tiptap/StatementViewer';
import AttemptCountdown from '../features/attempts/AttemptCountdown';
import {
  destroyAttemptAnswers,
  getAttemptAnswers,
  resetAttemptAnswers,
  subscribeAttemptAnswers,
} from '../features/attempts/attemptAnswerStore';
import TaskTestQuestion from '../features/task-test/TaskTestQuestion';

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
  entries.forEach(([, value]) => {
    const text = typeof value?.text === 'string' ? value.text : '';
    if (text) textParts.push(text);
    if (value?.selectedOptionKey) selectedCount += 1;
    if (Array.isArray(value?.selectedOptionKeys)) selectedCount += value.selectedOptionKeys.length;
  });
  const joinedText = textParts.join('\n');
  return {
    touched: entries.length,
    selectedCount,
    textLength: joinedText.length,
    textHash: hashActivityText(joinedText),
    textSample: joinedText.slice(0, 1200),
  };
}

function TaskTestSolve({ assignmentId, assignment, onActivity }) {
  const notify = useNotify();
  const [loading, setLoading] = useState(false);
  const [startData, setStartData] = useState(null);
  const [submitLoading, setSubmitLoading] = useState(false);
  const [result, setResult] = useState(null);
  const [limitReached, setLimitReached] = useState(false);
  const lastAnswerActivityRef = useRef({ signature: '', at: 0 });

  const storeKey = startData?.attemptId ? `test:${startData.attemptId}` : '';
  const questions = useMemo(() => startData?.questions ?? [], [startData?.questions]);

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
      onActivity?.('test_started', { payload: { kind: 'test' } });
      const data = await startTaskTest(assignmentId);
      resetAttemptAnswers(`test:${data.attemptId}`);
      lastAnswerActivityRef.current = { signature: '', at: 0 };
      setStartData(data);
    } catch (error) {
      if (error?.response?.status === 409) setLimitReached(true);
      notify.error(error?.userMessage || error?.message || 'Не удалось начать тест');
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
      onActivity?.('test_answers_final', {
        attemptId: startData.attemptId,
        textLength: summary.textLength,
        textHash: summary.textHash,
        textSample: summary.textSample,
        payload: { kind: 'test', touched: summary.touched, selectedCount: summary.selectedCount },
      });
      const payload = {
        attemptId: startData.attemptId,
        answers: Object.entries(answers).map(([questionId, value]) => ({
          questionId,
          selectedOptionKey: value?.selectedOptionKey ?? (value?.selectedOptionKeys?.[0] ?? null),
          selectedOptionKeys: value?.selectedOptionKeys ?? (value?.selectedOptionKey ? [value.selectedOptionKey] : null),
          text: value?.text ?? null,
        })),
      };
      const response = await submitTaskTest(assignmentId, payload);
      onActivity?.('test_finished', {
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
      notify.success(response.passed ? 'Тест засчитан ✅' : 'Попытка завершена');
    } catch (error) {
      onActivity?.('submit_failed', {
        attemptId: startData?.attemptId || null,
        payload: { kind: 'test', message: error?.userMessage || error?.message || 'Не удалось отправить ответы' },
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
      onActivity?.('test_answers_changed', {
        attemptId: startData?.attemptId,
        textLength: summary.textLength,
        textHash: summary.textHash,
        textSample: summary.textSample,
        payload: { kind: 'test', touched: summary.touched, selectedCount: summary.selectedCount },
      });
    });
  }, [onActivity, result, startData?.attemptId, storeKey]);

  const closeAttempt = useCallback(() => {
    if (storeKey) destroyAttemptAnswers(storeKey);
    setStartData(null);
    setResult(null);
  }, [storeKey]);

  return (
    <div className="max-w-4xl mx-auto space-y-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold">{assignment?.title || 'Тест'}</h1>
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
                startedAt={startData.startedAtUtc}
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
              Чтобы начать, нажми кнопку. Вопросы и варианты могут быть в случайном порядке.
            </div>
            <div className="flex gap-3">
              <Button onClick={begin} disabled={loading || limitReached}>
                {limitReached ? 'Лимит попыток' : (loading ? 'Запуск…' : 'Начать тест')}
              </Button>
            </div>
            {limitReached ? <div className="text-sm text-rose-600">Достигнут лимит попыток. Новую попытку начать нельзя.</div> : null}
          </div>
        </Card>
      ) : null}

      {result ? (
        <Card>
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <div className="text-lg font-semibold">
                Результат: {result.scorePercent}% ({result.correctQuestions}/{result.totalQuestions})
              </div>
              <div className="text-sm text-neutral-600 dark:text-neutral-400">
                Порог: {result.passPercent}%.{' '}
                {result.timeExpired ? '⏱️ Время вышло — попытка не засчитана.' : (result.passed ? '✅ Засчитано.' : '❌ Не засчитано.')}
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
          {questions.map((question, index) => (
            <TaskTestQuestion key={question.id} storeKey={storeKey} question={question} index={index} />
          ))}
          <div className="flex gap-3">
            <Button onClick={doSubmit} disabled={submitLoading}>{submitLoading ? 'Отправка…' : 'Завершить тест'}</Button>
            <Button variant="outline" onClick={closeAttempt} disabled={submitLoading}>Отмена</Button>
          </div>
        </div>
      ) : null}
    </div>
  );
}

export default React.memo(TaskTestSolve);
