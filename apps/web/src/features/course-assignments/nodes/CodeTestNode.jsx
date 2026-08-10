import React from 'react';
import { Code2 } from 'lucide-react';
import { AssignmentFooter, CourseMapHandles, NodeAccessBadges, NodeHover, NodeTopline } from './CourseMapNodePrimitives';

export default function CodeTestNode({ data, selected }) {
  const assignment = data?.entity || {};
  const solved = Boolean(assignment?.solvedByCurrentUser || assignment?.isSolved || assignment?.completedByCurrentUser || assignment?.progressStatus === 'solved');
  return (
    <div
      className={`course-map-node course-map-node--code${selected ? ' is-selected' : ''}${solved ? ' is-solved' : ''}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="assignment"
      data-taskforge-entity-id={assignment.id || data?.entityId}
      data-taskforge-assignment-type="code-test"
      data-taskforge-agent-state={solved ? 'solved' : 'unsolved'}
      data-taskforge-agent-action="open-assignment"
      aria-label={`Code test: ${assignment.title || 'Без названия'}`}
    >
      <CourseMapHandles />
      <NodeAccessBadges effects={data?.accessEffects} editorMode={data?.editorMode} />
      <NodeTopline icon={Code2} kicker="Code test" />
      <div className="course-map-node-title">{assignment.title || 'Без названия'}</div>
      <AssignmentFooter assignment={assignment} fallback="код" />
      <div className="course-map-code-watermark">{'{ }  ;'}</div>
      <NodeHover entity={assignment} meta={`Code test${assignment.language ? ` · ${assignment.language}` : ''}`} onAction={data?.onOpen} />
    </div>
  );
}
