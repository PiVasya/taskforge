import React from 'react';
import { Sigma } from 'lucide-react';
import { AssignmentFooter, CourseMapHandles, NodeAccessBadges, NodeHover, NodeTopline } from './CourseMapNodePrimitives';

export default function MathNode({ data, selected }) {
  const assignment = data?.entity || {};
  const solved = Boolean(assignment?.solvedByCurrentUser || assignment?.isSolved || assignment?.completedByCurrentUser || assignment?.progressStatus === 'solved');
  return (
    <div
      className={`course-map-node course-map-node--math${selected ? ' is-selected' : ''}${solved ? ' is-solved' : ''}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="assignment"
      data-taskforge-entity-id={assignment.id || data?.entityId}
      data-taskforge-assignment-type="math"
      data-taskforge-agent-state={solved ? 'solved' : 'unsolved'}
      data-taskforge-agent-action="open-assignment"
      aria-label={`Математическое задание: ${assignment.title || 'Без названия'}`}
    >
      <CourseMapHandles />
      <NodeAccessBadges effects={data?.accessEffects} editorMode={data?.editorMode} />
      <NodeTopline icon={Sigma} title={assignment.title} />
      <AssignmentFooter assignment={assignment} fallback="Задание" />
      <div className="course-map-math-watermark">Σ x² √</div>
      <NodeHover entity={assignment} onAction={data?.onOpen} />
    </div>
  );
}
