import React from 'react';
import { Badge, Card, Field, Input } from '../../components/ui';
import { QuizQuestionView, QuizChoiceView, quizHasInlineBlank } from './QuizRichView';

// The author sees the same read-only Tiptap renderer and question geometry as
// the learner, without opening or mutating an actual attempt.
export default function TaskTestLearnerPreview({ questions = [] }) {
  return (
    <div className="space-y-4" aria-label="Предпросмотр теста" role="region">
      {questions.map((question, index) => {
        const type = String(question.type || '').toLowerCase();
        const isChoice = type === 'single-choice' || type === 'multi-choice';
        const hasInlineBlank = type === 'fill' && quizHasInlineBlank(question);
        return (
          <Card key={question.id && question.id !== '00000000-0000-0000-0000-000000000000' ? question.id : index}>
            <div className="space-y-3">
              <div className="flex items-start justify-between gap-3">
                <div className="font-medium min-w-0 flex-1">
                  <div className="flex items-start gap-2">
                    <span className="shrink-0">{index + 1}.</span>
                    <div className="min-w-0 flex-1"><QuizQuestionView question={question} fill={hasInlineBlank ? { value: '', readOnly: true } : null} /></div>
                  </div>
                </div>
                <Badge variant="outline">{question.type}</Badge>
              </div>
              {isChoice ? (
                <div className={question.optionsLayout === 'inline' ? 'flex flex-wrap items-start gap-4' : 'space-y-2'}>
                  {(question.options || []).map((option, optionIndex) => (
                    <label key={option.key || optionIndex} className="flex min-w-0 items-start gap-2 text-sm">
                      <input type={type === 'single-choice' ? 'radio' : 'checkbox'}
                        name={`preview-q-${index}`} disabled aria-label={`Вариант ${optionIndex + 1}`} />
                      <div className="min-w-0 flex-1"><QuizChoiceView option={option} /></div>
                    </label>
                  ))}
                </div>
              ) : !hasInlineBlank ? (
                <Field label={type === 'fill' ? 'Вставь слово' : 'Ответ'}>
                  <Input disabled value="" readOnly />
                </Field>
              ) : null}
            </div>
          </Card>
        );
      })}
    </div>
  );
}
