import React from 'react';
import { Code2 } from 'lucide-react';
import { activateCourseMapAssignmentNode, AssignmentFooter, CourseMapHandles, NodeAccessBadges, NodeHover, NodeTopline } from './CourseMapNodePrimitives';

export default function CodeTestNode({ data, selected }) {
  const assignment = data?.entity || {};
  const solved = Boolean(assignment?.solvedByCurrentUser || assignment?.isSolved || assignment?.completedByCurrentUser || assignment?.progressStatus === 'solved');
  const language = assignment?.language || (Array.isArray(assignment?.allowedLanguages) ? assignment.allowedLanguages[0] : '');
  return (
    <div
      className={`course-map-node course-map-node--code${selected ? ' is-selected' : ''}${solved ? ' is-solved' : ''}`}
      data-taskforge-automation-id={`assignment-${assignment.id || data?.entityId}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="assignment"
      data-taskforge-entity-id={assignment.id || data?.entityId}
      data-taskforge-assignment-type="code-test"
      data-taskforge-agent-state={solved ? 'solved' : 'unsolved'}
      data-taskforge-agent-kind="code-test"
      data-taskforge-agent-action="open-assignment"
      onClick={(event) => activateCourseMapAssignmentNode(event, data, data?.onOpen)}
      onKeyDown={(event) => activateCourseMapAssignmentNode(event, data, data?.onOpen)}
      role={data?.editorMode ? undefined : "link"}
      tabIndex={data?.editorMode ? undefined : 0}
      aria-label={`Задание с кодом: ${assignment.title || 'Без названия'}`}
    >
      <CourseMapHandles />
      <NodeAccessBadges effects={data?.accessEffects} editorMode={data?.editorMode} />
      <NodeTopline icon={Code2} title={assignment.title} />
      <AssignmentFooter assignment={assignment} fallback="Код" />
      <div className="course-map-code-watermark">{'{ }  ;'}</div>
      <NodeHover entity={assignment} meta={language || null} onAction={data?.onOpen} />
    </div>
  );
}
