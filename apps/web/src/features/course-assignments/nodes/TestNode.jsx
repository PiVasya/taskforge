import React from 'react';
import { ListChecks } from 'lucide-react';
import { AssignmentFooter, CourseMapHandles, NodeAccessBadges, NodeHover, NodeTopline } from './CourseMapNodePrimitives';

export default function TestNode({ data, selected }) {
  const assignment = data?.entity || {};
  const solved = Boolean(assignment?.solvedByCurrentUser || assignment?.isSolved || assignment?.completedByCurrentUser || assignment?.progressStatus === 'solved');
  const questionCount = Array.isArray(assignment?.questions) ? assignment.questions.length : null;
  const questionLabel = questionCount == null
    ? 'Задание'
    : `${questionCount} ${questionCount === 1 ? 'вопрос' : questionCount > 1 && questionCount < 5 ? 'вопроса' : 'вопросов'}`;
  return (
    <div
      className={`course-map-node course-map-node--test${selected ? ' is-selected' : ''}${solved ? ' is-solved' : ''}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="assignment"
      data-taskforge-entity-id={assignment.id || data?.entityId}
      data-taskforge-assignment-type="test"
      data-taskforge-agent-state={solved ? 'solved' : 'unsolved'}
      data-taskforge-agent-action="open-assignment"
      aria-label={`Тестовое задание: ${assignment.title || 'Без названия'}`}
    >
      <CourseMapHandles />
      <NodeAccessBadges effects={data?.accessEffects} editorMode={data?.editorMode} />
      <NodeTopline icon={ListChecks} title={assignment.title} />
      <AssignmentFooter assignment={assignment} fallback={questionLabel} />
      <NodeHover entity={assignment} meta={questionCount != null ? questionLabel : null} onAction={data?.onOpen} />
    </div>
  );
}
