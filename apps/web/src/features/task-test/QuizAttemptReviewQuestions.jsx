import React from 'react';
import { Badge } from '../../components/ui';
import { QuizQuestionView, QuizChoiceView, QuizExplanationView, quizHasInlineBlank } from './QuizRichView';

// The submitted attempt's review, not the public question list, is the only
// source for answer keys and explanations. Callers render this after submission.
export default function QuizAttemptReviewQuestions({ questions = [] }) {
  if (!Array.isArray(questions) || questions.length === 0) return null;
  return (
    <div className="space-y-4" data-taskforge-agent-role="test-attempt-review">
      {questions.map((question, index) => {
        const type = String(question.type || '').toLowerCase();
        const isCorrect = Boolean(question.isCorrect);
        const answer = question.userAnswer || {};
        const userText = String(answer.text || '');
        const hasBlank = type === 'fill' && quizHasInlineBlank(question);
        const selected = new Set(Array.isArray(answer.selectedOptionKeys) && answer.selectedOptionKeys.length
          ? answer.selectedOptionKeys : answer.selectedOptionKey ? [answer.selectedOptionKey] : []);
        const correctKeys = new Set(Array.isArray(question.correctOptionKeys) ? question.correctOptionKeys : []);
        const options = Array.isArray(question.options) ? question.options : [];
        const isChoice = type === 'single-choice' || type === 'multi-choice';
        return (
          <div key={question.id || index} className="rounded-xl border border-[rgb(var(--border))] p-4 bg-[rgb(var(--card))]" data-taskforge-question-id={question.id || undefined}>
            <div className="flex items-start justify-between gap-3">
              <div className="font-medium min-w-0 flex-1">
                <span className="mr-2">{index + 1}.</span>
                <QuizQuestionView question={question} fill={hasBlank ? { value: userText, readOnly: true } : null} />
              </div>
              <Badge intent={isCorrect ? 'success' : 'danger'}>{isCorrect ? 'Верно' : 'Неверно'}</Badge>
            </div>
            {isChoice ? (
              <div className={question.optionsLayout === 'inline' ? 'mt-3 flex flex-wrap items-start gap-4' : 'mt-3 space-y-2'}>
                {options.map((option) => {
                  const isSelected = selected.has(option.key);
                  const isCorrectOption = correctKeys.has(option.key);
                  const className = [
                    'flex items-center gap-2 text-sm min-w-0',
                    isCorrectOption ? 'text-emerald-700 dark:text-emerald-300 font-medium' : '',
                    isSelected && !isCorrectOption ? 'text-rose-700 dark:text-rose-300' : '',
                  ].filter(Boolean).join(' ');
                  return (
                    <div key={option.key} className={className}>
                      <input type={type === 'multi-choice' ? 'checkbox' : 'radio'} checked={isSelected} readOnly />
                      <div className="min-w-0 flex-1"><QuizChoiceView option={option} /></div>
                      {isCorrectOption ? <span className="text-xs opacity-80">(правильный)</span> : null}
                      {isSelected && !isCorrectOption ? <span className="text-xs opacity-80">(ваш выбор)</span> : null}
                    </div>
                  );
                })}
              </div>
            ) : null}
            {(type === 'text' || type === 'fill') ? (
              <div className="mt-3 space-y-2 text-sm">
                {!hasBlank ? (
                  <div><span className="text-neutral-500 dark:text-neutral-400">Ваш ответ:</span>{' '}
                    {userText ? <span style={{ whiteSpace: 'pre-wrap' }}>{userText}</span> : <i>—</i>}
                  </div>
                ) : null}
                {Array.isArray(question.acceptedAnswers) && question.acceptedAnswers.length > 0 ? (
                  <div><span className="text-neutral-500 dark:text-neutral-400">Правильные ответы:</span>{' '}
                    <span style={{ whiteSpace: 'pre-wrap' }}>{question.acceptedAnswers.join(', ')}</span>
                  </div>
                ) : null}
              </div>
            ) : null}
            <QuizExplanationView question={question} />
          </div>
        );
      })}
    </div>
  );
}
