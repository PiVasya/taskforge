import React from 'react';
import StatementEditor from '../../components/tiptap/StatementEditor';
import { QuizExplanationView } from './QuizRichView';
import { hasQuizContent, quizRichValue } from './quizRichContent';

export default function TaskTestExplanationEditor({ question, index, active, onActivate, onChange, imageFolder }) {
  const editorKey = `explanation:${index}`;
  return (
    <div className="mt-4 space-y-2">
      <div className="text-sm font-medium text-[rgb(var(--text-muted))]">Пояснение после ответа</div>
      {active ? (
        <StatementEditor
          imageFolder={imageFolder}
          key={editorKey}
          value={quizRichValue(question.explanationContentJson)}
          mode="rich"
          compact
          preserveLiteral
          onChange={onChange}
        />
      ) : (
        <div
          className="quiz-editor-preview min-h-[40px] rounded-lg border border-[rgb(var(--border))] px-3 py-2"
          role="button"
          tabIndex={0}
          aria-label={`Редактировать пояснение к вопросу ${index + 1}`}
          data-taskforge-agent-role="test-explanation-editor"
          onClick={onActivate}
          onKeyDown={(event) => {
            if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault();
              onActivate();
            }
          }}
        >
          {hasQuizContent(question.explanationContentJson)
            ? <QuizExplanationView question={question} showLabel={false} />
            : null}
        </div>
      )}
    </div>
  );
}
