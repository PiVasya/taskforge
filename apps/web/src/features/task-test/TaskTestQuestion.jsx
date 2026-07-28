import React, { useMemo } from 'react';
import { Badge, Card, Field, Input } from '../../components/ui';
import { setAttemptAnswer, useAttemptAnswer } from '../attempts/attemptAnswerStore';

function splitFillPrompt(prompt) {
  const value = String(prompt || '');
  const match = value.match(/_{3,}/);
  if (!match) return null;
  const blank = match[0];
  const index = value.indexOf(blank);
  return {
    before: value.slice(0, index),
    after: value.slice(index + blank.length),
    blankLen: blank.length,
  };
}

function TaskTestQuestion({ storeKey, question, index }) {
  const answer = useAttemptAnswer(storeKey, question.id);
  const type = String(question.type || '').toLowerCase();
  const fillParts = useMemo(
    () => (type === 'fill' ? splitFillPrompt(question.prompt) : null),
    [question.prompt, type],
  );

  return (
    <Card>
      <div className="space-y-3">
        <div className="flex items-start justify-between gap-3">
          <div className="font-medium">
            {index + 1}.{' '}
            {type === 'fill' && fillParts ? (
              <span className="fill-line">
                <span className="whitespace-pre-wrap">{fillParts.before}</span>
                <input
                  className="fill-input"
                  value={answer.text || ''}
                  placeholder=""
                  onChange={(event) => setAttemptAnswer(storeKey, question.id, { text: event.target.value })}
                  style={{ width: `${Math.min(30, Math.max(6, (fillParts.blankLen || 3) * 2))}ch` }}
                />
                <span className="whitespace-pre-wrap">{fillParts.after}</span>
              </span>
            ) : (
              <span className="whitespace-pre-wrap">{question.prompt}</span>
            )}
          </div>
          <Badge variant="outline">{question.type}</Badge>
        </div>

        {type === 'single-choice' ? (
          <div className="space-y-2">
            {(question.options || []).map((option) => (
              <label key={option.key} className="flex items-center gap-2 text-sm cursor-pointer">
                <input
                  type="radio"
                  name={`q-${question.id}`}
                  checked={(answer.selectedOptionKey || '') === option.key}
                  onChange={() => setAttemptAnswer(storeKey, question.id, { selectedOptionKey: option.key })}
                />
                <span>{option.text}</span>
              </label>
            ))}
          </div>
        ) : null}

        {type === 'multi-choice' ? (
          <div className="space-y-2">
            {(question.options || []).map((option) => {
              const selected = Array.isArray(answer.selectedOptionKeys) ? answer.selectedOptionKeys : [];
              const checked = selected.includes(option.key);
              return (
                <label key={option.key} className="flex items-center gap-2 text-sm cursor-pointer">
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => setAttemptAnswer(storeKey, question.id, (current) => {
                      const next = new Set(current.selectedOptionKeys || []);
                      if (next.has(option.key)) next.delete(option.key);
                      else next.add(option.key);
                      return { selectedOptionKeys: Array.from(next) };
                    })}
                  />
                  <span>{option.text}</span>
                </label>
              );
            })}
            <div className="text-xs text-neutral-600 dark:text-neutral-400">Можно выбрать несколько вариантов.</div>
          </div>
        ) : null}

        {type === 'text' ? (
          <Field label="Ответ">
            <Input
              value={answer.text || ''}
              onChange={(event) => setAttemptAnswer(storeKey, question.id, { text: event.target.value })}
              placeholder="Введите ответ…"
            />
          </Field>
        ) : null}

        {type === 'fill' && !fillParts ? (
          <Field label="Вставь слово">
            <Input
              value={answer.text || ''}
              onChange={(event) => setAttemptAnswer(storeKey, question.id, { text: event.target.value })}
              placeholder="Введите слово…"
            />
          </Field>
        ) : null}
      </div>
    </Card>
  );
}

export default React.memo(TaskTestQuestion);
