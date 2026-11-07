import React, { useMemo } from 'react';
import { QuizQuestionView, QuizChoiceView, quizHasInlineBlank } from './QuizRichView';
import { Badge, Card, Field, Input } from '../../components/ui';
import { setAttemptAnswer, useAttemptAnswer } from '../attempts/attemptAnswerStore';

function automationPart(value, fallback) {
  const normalized = String(value ?? '').trim().replace(/[^a-zA-Z0-9_.:-]+/g, '-').replace(/^-+|-+$/g, '');
  return (normalized || fallback).slice(0, 100);
}

function questionAutomation(question, index) {
  return `test-q-${automationPart(question?.id, String(index + 1))}`;
}

function commonQuestionAttrs(question, index) {
  return {
    'data-taskforge-question-index': index + 1,
    'data-taskforge-question-id': String(question?.id || ''),
  };
}

function TaskTestQuestion({ storeKey, question, index }) {
  const answer = useAttemptAnswer(storeKey, question.id);
  const type = String(question.type || '').toLowerCase();
  const automationId = questionAutomation(question, index);
  const questionAttrs = commonQuestionAttrs(question, index);
  const hasInlineBlank = useMemo(() => type === 'fill' && quizHasInlineBlank(question), [question, type]);

  return (
    <Card
      data-taskforge-automation-id={automationId}
      data-taskforge-agent-role="test-question"
      data-taskforge-agent-kind={type}
      {...questionAttrs}
    >
      <div className="space-y-3">
        <div className="flex items-start justify-between gap-3">
          <div className="font-medium min-w-0 flex-1">
            <div className="flex items-start gap-2">
              <span className="shrink-0">{index + 1}.</span>
              <div className="min-w-0 flex-1">
            <QuizQuestionView
              question={question}
              fill={hasInlineBlank ? {
                value: answer.text || '',
                onChange: (value) => setAttemptAnswer(storeKey, question.id, { text: value }),
                ariaLabel: `Вопрос ${index + 1}: текстовый ответ`,
                attributes: {
                  'data-taskforge-automation-id': `${automationId}-answer`,
                  'data-taskforge-agent-role': 'test-answer-input',
                  'data-taskforge-agent-action': 'fill-test-answer',
                  'data-taskforge-agent-kind': type,
                  ...questionAttrs,
                },
              } : null}
            />
              </div>
            </div>
          </div>
          <Badge variant="outline">{question.type}</Badge>
        </div>

        {type === 'single-choice' ? (
          <div className={question.optionsLayout === 'inline' ? "flex flex-wrap items-start gap-4" : "space-y-2"}>
            {(question.options || []).map((option, optionIndex) => (
              <label key={option.key} className="flex min-w-0 items-start gap-2 text-sm cursor-pointer">
                <input
                  type="radio"
                  name={`q-${question.id}`}
                  checked={(answer.selectedOptionKey || '') === option.key}
                  aria-label={`Вопрос ${index + 1}, вариант ${optionIndex + 1}: ${option.text}`}
                  data-taskforge-automation-id={`${automationId}-option-${optionIndex + 1}`}
                  data-taskforge-agent-role="test-answer-option"
                  data-taskforge-agent-action="select-test-answer"
                  data-taskforge-agent-kind="single-choice"
                  data-taskforge-option-index={optionIndex + 1}
                  data-taskforge-option-key={String(option.key ?? '')}
                  {...questionAttrs}
                  onChange={() => setAttemptAnswer(storeKey, question.id, { selectedOptionKey: option.key })}
                />
                <div className="min-w-0 flex-1"><QuizChoiceView option={option} /></div>
              </label>
            ))}
          </div>
        ) : null}

        {type === 'multi-choice' ? (
          <div className={question.optionsLayout === 'inline' ? "flex flex-wrap items-start gap-4" : "space-y-2"}>
            {(question.options || []).map((option, optionIndex) => {
              const selected = Array.isArray(answer.selectedOptionKeys) ? answer.selectedOptionKeys : [];
              const checked = selected.includes(option.key);
              return (
                <label key={option.key} className="flex min-w-0 items-start gap-2 text-sm cursor-pointer">
                  <input
                    type="checkbox"
                    checked={checked}
                    aria-label={`Вопрос ${index + 1}, вариант ${optionIndex + 1}: ${option.text}`}
                    data-taskforge-automation-id={`${automationId}-option-${optionIndex + 1}`}
                    data-taskforge-agent-role="test-answer-option"
                    data-taskforge-agent-action="toggle-test-answer"
                    data-taskforge-agent-kind="multi-choice"
                    data-taskforge-option-index={optionIndex + 1}
                    data-taskforge-option-key={String(option.key ?? '')}
                    {...questionAttrs}
                    onChange={() => setAttemptAnswer(storeKey, question.id, (current) => {
                      const next = new Set(current.selectedOptionKeys || []);
                      if (next.has(option.key)) next.delete(option.key);
                      else next.add(option.key);
                      return { selectedOptionKeys: Array.from(next) };
                    })}
                  />
                  <div className="min-w-0 flex-1"><QuizChoiceView option={option} /></div>
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
              aria-label={`Вопрос ${index + 1}: текстовый ответ`}
              data-taskforge-automation-id={`${automationId}-answer`}
              data-taskforge-agent-role="test-answer-input"
              data-taskforge-agent-action="fill-test-answer"
              data-taskforge-agent-kind="text"
              {...questionAttrs}
              onChange={(event) => setAttemptAnswer(storeKey, question.id, { text: event.target.value })}
              placeholder="Введите ответ…"
            />
          </Field>
        ) : null}

        {type === 'fill' && !hasInlineBlank ? (
          <Field label="Вставь слово">
            <Input
              value={answer.text || ''}
              aria-label={`Вопрос ${index + 1}: заполнить пропуск`}
              data-taskforge-automation-id={`${automationId}-answer`}
              data-taskforge-agent-role="test-answer-input"
              data-taskforge-agent-action="fill-test-answer"
              data-taskforge-agent-kind="fill"
              {...questionAttrs}
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
