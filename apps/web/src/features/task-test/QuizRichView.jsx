import React from 'react';
import StaticStatementViewer from '../../components/tiptap/StaticStatementViewer';
import { quizRichValue, quizHasInlineBlank } from './quizRichContent';

const QuizQuestionView = React.memo(function QuizQuestionView({ question, fill = null }) {
  const content = quizRichValue(question?.promptContentJson, question?.prompt);
  return <StaticStatementViewer value={content} preserveLiteral fill={fill} />;
});

const QuizChoiceView = React.memo(function QuizChoiceView({ option }) {
  const content = quizRichValue(option?.contentJson, option?.text);
  return <StaticStatementViewer value={content} preserveLiteral />;
});

export { quizHasInlineBlank };
export { QuizQuestionView, QuizChoiceView };
