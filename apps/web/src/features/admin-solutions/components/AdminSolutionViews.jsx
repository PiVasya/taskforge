import React from 'react';
import { Badge, Card } from '../../../components/ui';
import {
  getRunnerText,
  getSolutionCases,
  isResultCasePassed as isResultCasePassedStrict,
} from '../../../utils/solutionDto';

export function CompactEmpty({ children }) {
  return (
    <div className="rounded-xl border border-dashed border-neutral-300 dark:border-neutral-700 px-4 py-3 text-sm text-neutral-500 dark:text-neutral-400">
      {children}
    </div>
  );
}

export function RunnerOutput({ item }) {
  const stdout = getRunnerText(item, 'stdout');
  const stderr = getRunnerText(item, 'stderr');
  const runnerError = getRunnerText(item, 'runnerError') || getRunnerText(item, 'error');
  const cases = getSolutionCases(item);

  if (!stdout && !stderr && !runnerError && !cases.length) return null;

  return (
    <Card className="p-3 space-y-3">
      {runnerError ? <div className="text-sm text-red-600 whitespace-pre-wrap">{runnerError}</div> : null}
      {stdout ? (
        <div>
          <div className="text-xs uppercase tracking-wide text-neutral-500">stdout</div>
          <pre className="text-xs whitespace-pre-wrap rounded-lg border border-neutral-200 dark:border-neutral-700 p-3 mt-1">{stdout}</pre>
        </div>
      ) : null}
      {stderr ? (
        <div>
          <div className="text-xs uppercase tracking-wide text-neutral-500">stderr</div>
          <pre className="text-xs whitespace-pre-wrap rounded-lg border border-neutral-200 dark:border-neutral-700 p-3 mt-1">{stderr}</pre>
        </div>
      ) : null}
      {cases.length ? (
        <div className="space-y-2">
          <div className="text-xs uppercase tracking-wide text-neutral-500">Тесты</div>
          {cases.slice(0, 8).map((testCase, index) => {
            const passed = isResultCasePassedStrict(testCase);
            return (
              <div key={index} className="rounded-lg border border-neutral-200 dark:border-neutral-700 p-2 text-xs">
                <div className="flex items-center justify-between gap-2">
                  <span>Тест #{index + 1}</span>
                  <Badge intent={passed ? 'success' : 'danger'}>{passed ? 'OK' : 'FAIL'}</Badge>
                </div>
                {(testCase?.stderr || testCase?.compileStderr || testCase?.error) ? (
                  <pre className="mt-2 whitespace-pre-wrap break-words text-red-600">
                    {testCase.stderr || testCase.compileStderr || testCase.error}
                  </pre>
                ) : null}
              </div>
            );
          })}
          {cases.length > 8 ? <div className="text-xs text-neutral-500">… и ещё {cases.length - 8}</div> : null}
        </div>
      ) : null}
    </Card>
  );
}

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

export const TestAttemptReview = React.memo(function TestAttemptReview({ dto }) {
  if (!dto) return null;
  const questions = Array.isArray(dto.questions) ? dto.questions : [];

  return (
    <div className="mt-4 space-y-4">
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <Badge intent={dto.passed ? 'success' : 'danger'}>{dto.passed ? 'Зачёт' : 'Не зачтено'}</Badge>
        <Badge intent="secondary">
          {dto.scorePercent}% • {dto.correctQuestions}/{dto.totalQuestions}
        </Badge>
        {dto.timeExpired ? <Badge intent="danger">Время вышло</Badge> : null}
      </div>

      <div className="space-y-4">
        {questions.map((question, index) => {
          const type = String(question.type || '').toLowerCase();
          const isCorrect = Boolean(question.isCorrect);
          const userAnswer = question.userAnswer || {};
          const split = type === 'fill' ? splitFillPrompt(question.prompt) : null;
          const userText = String(userAnswer.text || '');
          const selected = new Set(Array.isArray(userAnswer.selectedOptionKeys) ? userAnswer.selectedOptionKeys : []);
          const correctKeys = new Set(Array.isArray(question.correctOptionKeys) ? question.correctOptionKeys : []);
          const options = Array.isArray(question.options) ? question.options : [];

          return (
            <div
              key={question.id || index}
              className="rounded-xl border border-neutral-200 dark:border-neutral-700 p-4 bg-[rgb(var(--card))]"
            >
              <div className="flex items-start justify-between gap-3">
                <div className="font-medium">
                  {index + 1}.{' '}
                  {split ? (
                    <span className="fill-line">
                      {split.before}
                      <input
                        className="fill-input"
                        value={userText}
                        readOnly
                        style={{ width: `${Math.min(30, Math.max(6, (split.blankLen || 3) * 2))}ch` }}
                      />
                      {split.after}
                    </span>
                  ) : (
                    <span>{question.prompt}</span>
                  )}
                </div>
                <Badge intent={isCorrect ? 'success' : 'danger'}>{isCorrect ? 'Верно' : 'Неверно'}</Badge>
              </div>

              {(type === 'single-choice' || type === 'multi-choice') ? (
                <div className="mt-3 space-y-2">
                  {options.map((option) => {
                    const isSelected = selected.has(option.key);
                    const isCorrectOption = correctKeys.has(option.key);
                    const className = [
                      'flex items-center gap-2 text-sm',
                      isCorrectOption ? 'text-emerald-700 dark:text-emerald-300 font-medium' : '',
                      isSelected && !isCorrectOption ? 'text-rose-700 dark:text-rose-300' : '',
                    ].filter(Boolean).join(' ');

                    return (
                      <div key={option.key} className={className}>
                        <input type={type === 'multi-choice' ? 'checkbox' : 'radio'} checked={isSelected} readOnly />
                        <span>{option.text}</span>
                        {isCorrectOption ? <span className="text-xs opacity-80">(правильный)</span> : null}
                        {isSelected && !isCorrectOption ? <span className="text-xs opacity-80">(выбрано)</span> : null}
                      </div>
                    );
                  })}
                </div>
              ) : null}

              {(type === 'text' || type === 'fill') && !split ? (
                <div className="mt-3 space-y-2 text-sm">
                  <div>
                    <span className="text-neutral-500 dark:text-neutral-400">Ответ:</span>{' '}
                    {userText || <i>—</i>}
                  </div>
                  {Array.isArray(question.acceptedAnswers) && question.acceptedAnswers.length > 0 ? (
                    <div>
                      <span className="text-neutral-500 dark:text-neutral-400">Правильные ответы:</span>{' '}
                      {question.acceptedAnswers.join(', ')}
                    </div>
                  ) : null}
                </div>
              ) : null}
            </div>
          );
        })}
      </div>
    </div>
  );
});
