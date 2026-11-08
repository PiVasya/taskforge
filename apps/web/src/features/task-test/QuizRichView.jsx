import React from 'react';
import StaticStatementViewer from '../../components/tiptap/StaticStatementViewer';
import { quizRichValue, quizHasInlineBlank, hasQuizContent } from './quizRichContent';

const QuizQuestionView = React.memo(function QuizQuestionView({ question, fill = null }) {
  const content = quizRichValue(question?.promptContentJson, question?.prompt);
  return <StaticStatementViewer value={content} preserveLiteral fill={fill} />;
});

const QuizChoiceView = React.memo(function QuizChoiceView({ option }) {
  const content = quizRichValue(option?.contentJson, option?.text);
  return <StaticStatementViewer value={content} preserveLiteral />;
});

const QuizExplanationView = React.memo(function QuizExplanationView({ question, showLabel = true }) {
  if (!question?.explanationContentJson || !hasQuizContent(question.explanationContentJson)) return null;
  return (
    <div className={showLabel ? "mt-3 min-w-0 border-t border-[rgb(var(--border))] pt-3" : "min-w-0"} data-taskforge-agent-role="test-answer-explanation">
      {showLabel ? <div className="text-sm font-semibold mb-2">Пояснение</div> : null}
      <StaticStatementViewer value={quizRichValue(question.explanationContentJson)} preserveLiteral />
    </div>
  );
});

export { quizHasInlineBlank };
export { QuizQuestionView, QuizChoiceView, QuizExplanationView };
