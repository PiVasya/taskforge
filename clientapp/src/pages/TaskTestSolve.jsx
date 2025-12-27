import React, { useEffect, useMemo, useState } from 'react';
import { Card, Button, Field, Input, Textarea, Select, Badge } from '../components/ui';
import { startTaskTest, submitTaskTest } from '../api/taskTests';
import { useNotify } from '../components/notify/NotifyProvider';

function fmtSeconds(total) {
  if (total == null) return '';
  const t = Math.max(0, Math.floor(total));
  const m = Math.floor(t / 60);
  const s = t % 60;
  return `${m}:${String(s).padStart(2, '0')}`;
}

export default function TaskTestSolve({ assignmentId, assignment }) {
  const notify = useNotify();

  const [loading, setLoading] = useState(false);
  const [startData, setStartData] = useState(null);
  const [answers, setAnswers] = useState({});

  const [submitLoading, setSubmitLoading] = useState(false);
  const [result, setResult] = useState(null);

  // Если лимит попыток достигнут, показываем предупреждение и блокируем "Начать тест".
  // Важно именно для кейса: пользователь закрыл результаты, увидел кнопку "Начать тест",
  // нажал её, получил 409, но UI может выглядеть как будто тест всё равно начался.
  const [limitReached, setLimitReached] = useState(false);

  const timeLimit = startData?.timeLimitSeconds ?? null;
  const startedAt = startData?.startedAtUtc ? new Date(startData.startedAtUtc) : null;

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
      // Важно: сбрасываем старые данные попытки ДО запроса.
      // Иначе при 409 (лимит попыток) UI покажет старые вопросы как будто тест начался,
      // но отправка уже не сработает (attemptId от старой попытки).
      setStartData(null);
      setResult(null);
      setAnswers({});
      const data = await startTaskTest(assignmentId);
      setStartData(data);
    } catch (err) {
      if (err?.response?.status === 409) {
        setLimitReached(true);
      }
      notify.error(err?.userMessage || err?.message || 'Не удалось начать тест');
    } finally {
      setLoading(false);
    }
  };

  // Для типа "fill" (вставить пропущенное слово) поддерживаем плейсхолдер из подчёркиваний,
  // например: "______ самый быстрый язык".
  // В UI вставляем поле ввода прямо в текст вопроса.
  const splitFillPrompt = (prompt) => {
    const p = String(prompt || '');
    // Берём первую группу подчёркиваний (___). Достаточно даже одного, но по UX обычно 3+.
    const m = p.match(/_+/);
    if (!m) return null;
    const blank = m[0];
    const i = p.indexOf(blank);
    return { before: p.slice(0, i), after: p.slice(i + blank.length), blankLen: blank.length };
  };

  const doSubmit = async () => {
    if (!startData?.attemptId) return;
    try {
      setSubmitLoading(true);
      const payload = {
        attemptId: startData.attemptId,
        answers: Object.entries(answers).map(([questionId, v]) => ({
          questionId,
          // single-choice: selectedOptionKey (и дублируем массивом)
          selectedOptionKey: v?.selectedOptionKey ?? (v?.selectedOptionKeys?.[0] ?? null),
          // multi-choice: selectedOptionKeys (и поддержка старого формата через selectedOptionKey)
          selectedOptionKeys:
            v?.selectedOptionKeys ?? (v?.selectedOptionKey ? [v.selectedOptionKey] : null),
          text: v?.text ?? null,
        })),
      };
      const res = await submitTaskTest(assignmentId, payload);
      setResult(res);

      // Если это была последняя попытка — запоминаем это, чтобы после закрытия
      // результатов пользователь не мог "начать" тест повторно.
      if (
        Number.isFinite(startData?.attemptNumber) &&
        Number.isFinite(startData?.maxAttempts) &&
        startData.attemptNumber >= startData.maxAttempts
      ) {
        setLimitReached(true);
      }
      notify.success(res.passed ? 'Тест засчитан ✅' : 'Попытка завершена');
    } catch (err) {
      notify.error(err?.userMessage || err?.message || 'Не удалось отправить ответы');
    } finally {
      setSubmitLoading(false);
    }
  };

  // автосабмит при 0 (мягко)
  useEffect(() => {
    if (!startData?.attemptId) return;
    if (secondsLeft == null) return;
    if (secondsLeft > 0) return;
    // чтобы не заспамить
    if (submitLoading || result) return;
    doSubmit();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [secondsLeft]);

  const questions = startData?.questions ?? [];

  return (
    <div className="max-w-4xl mx-auto space-y-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold">{assignment?.title || 'Тест'}</h1>
          {assignment?.description && (
            <p className="mt-2 text-sm text-slate-600 dark:text-slate-400 whitespace-pre-wrap">
              {assignment.description}
            </p>
          )}
        </div>
        <div className="text-right">
          {startData && (
            <div className="text-sm text-slate-600 dark:text-slate-400">
              Попытка: <b>{startData.attemptNumber}</b> / {startData.maxAttempts}
            </div>
          )}
          {timeLimit ? (
            <div className="mt-1">
              <Badge variant={secondsLeft !== null && secondsLeft <= 10 ? 'destructive' : 'secondary'}>
                Таймер: {fmtSeconds(secondsLeft)}
              </Badge>
            </div>
          ) : null}
        </div>
      </div>

      {!startData && (
        <Card>
          <div className="space-y-3">
            <div className="text-sm text-slate-600 dark:text-slate-400">
              Чтобы начать, нажми кнопку. Вопросы/варианты могут быть в случайном порядке.
            </div>
            <div className="flex gap-3">
              <Button onClick={begin} disabled={loading || limitReached}>
                {limitReached ? 'Лимит попыток' : (loading ? 'Запуск…' : 'Начать тест')}
              </Button>
            </div>
            {limitReached ? (
              <div className="text-sm text-rose-600">
                Достигнут лимит попыток. Новую попытку начать нельзя.
              </div>
            ) : null}
          </div>
        </Card>
      )}

      {result && (
        <Card>
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <div className="text-lg font-semibold">
                Результат: {result.scorePercent}% ({result.correctCount}/{result.totalCount})
              </div>
              <div className="text-sm text-slate-600 dark:text-slate-400">
                Порог: {result.passPercent}%.{' '}
                {result.timeExpired ? '⏱️ Время вышло — попытка не засчитана.' : (result.passed ? '✅ Засчитано.' : '❌ Не засчитано.')}
              </div>
            </div>
            <div className="flex gap-3">
              <Button variant="outline" onClick={() => { setStartData(null); setResult(null); }}>
                Закрыть
              </Button>
              {startData && startData.attemptNumber < startData.maxAttempts ? (
                <Button onClick={begin} disabled={loading}>
                  {loading ? 'Запуск…' : 'Новая попытка'}
                </Button>
              ) : (
                <Button variant="secondary" disabled>
                  Лимит попыток
                </Button>
              )}
            </div>
          </div>
        </Card>
      )}

      {startData && !result && (
        <div className="space-y-4">
          {questions.map((q, idx) => (
            <Card key={q.id}>
              <div className="space-y-3">
                <div className="flex items-start justify-between gap-3">
                  <div className="font-medium whitespace-pre-wrap">
                    {idx + 1}.{' '}
                    {(() => {
                      const type = (q.type || '').toLowerCase();
                      if (type !== 'fill') return q.prompt;

                      const parts = splitFillPrompt(q.prompt);
                      if (!parts) return q.prompt;

                      const val = answers[q.id]?.text || '';
                      const ch = Math.min(40, Math.max(6, (parts.blankLen || 3) * 2)); // width = count * 2ch (c clamp)
                      return (
                        <span className="fill-line">
                          <span className="whitespace-pre-wrap">{parts.before}</span>
                          <input
                            className="fill-input"
                            value={val}
                            placeholder=""
                            onChange={(e) =>
                              setAnswers((p) => ({
                                ...p,
                                [q.id]: { text: e.target.value },
                              }))
                            }
                            style={{ width: `${ch}ch` }}
                          />
                          <span className="whitespace-pre-wrap">{parts.after}</span>
                        </span>
                      );
                    })()}
                  </div>
                  <Badge variant="outline">{q.type}</Badge>
                </div>

                {q.type === 'single-choice' && (
                  <div className="space-y-2">
                    {(q.options || []).map((o) => {
                      const cur = answers[q.id]?.selectedOptionKey || '';
                      return (
                        <label key={o.key} className="flex items-center gap-2 text-sm cursor-pointer">
                          <input
                            type="radio"
                            name={`q-${q.id}`}
                            checked={cur === o.key}
                            onChange={() => setAnswers((p) => ({ ...p, [q.id]: { selectedOptionKey: o.key } }))}
                          />
                          <span>{o.text}</span>
                        </label>
                      );
                    })}
                  </div>
                )}

                {q.type === 'multi-choice' && (
                  <div className="space-y-2">
                    {(q.options || []).map((o) => {
                      const cur = new Set(answers[q.id]?.selectedOptionKeys || []);
                      const checked = cur.has(o.key);
                      return (
                        <label key={o.key} className="flex items-center gap-2 text-sm cursor-pointer">
                          <input
                            type="checkbox"
                            checked={checked}
                            onChange={() =>
                              setAnswers((p) => {
                                const prev = new Set(p[q.id]?.selectedOptionKeys || []);
                                if (prev.has(o.key)) prev.delete(o.key);
                                else prev.add(o.key);
                                return { ...p, [q.id]: { selectedOptionKeys: Array.from(prev) } };
                              })
                            }
                          />
                          <span>{o.text}</span>
                        </label>
                      );
                    })}
                    <div className="text-xs text-slate-600 dark:text-slate-400">
                      Можно выбрать несколько вариантов.
                    </div>
                  </div>
                )}

                {q.type === 'text' && (
                  <Field label="Ответ">
                    <Input
                      value={answers[q.id]?.text || ''}
                      onChange={(e) => setAnswers((p) => ({ ...p, [q.id]: { text: e.target.value } }))}
                      placeholder="Введите ответ…"
                    />
                  </Field>
                )}

                {q.type === 'fill' && !splitFillPrompt(q.prompt) && (
                  <Field label="Вставь слово">
                    <Input
                      value={answers[q.id]?.text || ''}
                      onChange={(e) => setAnswers((p) => ({ ...p, [q.id]: { text: e.target.value } }))}
                      placeholder="Введите слово…"
                    />
                  </Field>
                )}
              </div>
            </Card>
          ))}

          <div className="flex gap-3">
            <Button onClick={doSubmit} disabled={submitLoading}>
              {submitLoading ? 'Отправка…' : 'Завершить тест'}
            </Button>
            <Button variant="outline" onClick={() => { setStartData(null); setAnswers({}); }} disabled={submitLoading}>
              Отмена
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
