import React from 'react';
import { Sigma } from 'lucide-react';
import { AssignmentFooter, CourseMapHandles, NodeHover, NodeTopline } from './CourseMapNodePrimitives';

export default function MathNode({ data, selected }) {
  const assignment = data?.entity || {};
  return (
    <div
      className={`course-map-node course-map-node--math${selected ? ' is-selected' : ''}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="assignment"
      data-taskforge-entity-id={assignment.id || data?.entityId}
      data-taskforge-assignment-type="math"
      data-taskforge-agent-state={assignment?.solvedByCurrentUser || assignment?.isSolved ? 'solved' : 'unsolved'}
      data-taskforge-agent-action="open-assignment"
      aria-label={`Математика: ${assignment.title || 'Без названия'}`}
    >
      <CourseMapHandles />
      <NodeTopline icon={Sigma} kicker="Математика" badge="β" />
      <div className="course-map-node-title">{assignment.title || 'Без названия'}</div>
      <AssignmentFooter assignment={assignment} fallback="в разработке" />
      <div className="course-map-math-watermark">Σ x² √</div>
      <NodeHover entity={assignment} meta="Математика · β" onAction={data?.onOpen} />
    </div>
  );
}
