import React from 'react';
import StatementViewer from '../../components/tiptap/StatementViewer';
import { quizRichValue, quizHasInlineBlank } from './quizRichContent';

export function QuizQuestionView({ question, fill = null }) {
  const content = quizRichValue(question?.promptContentJson, question?.prompt);
  return <StatementViewer value={content} staticOnly preserveLiteral fill={fill} />;
}

export function QuizChoiceView({ option }) {
  const content = quizRichValue(option?.contentJson, option?.text);
  return <StatementViewer value={content} staticOnly preserveLiteral />;
}

export { quizHasInlineBlank };
