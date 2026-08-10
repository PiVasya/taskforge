import React from 'react';
import { Image } from 'lucide-react';
import { AssignmentFooter, CourseMapHandles, NodeAccessBadges, NodeHover, NodeTopline } from './CourseMapNodePrimitives';

export default function ImageCodeNode({ data, selected }) {
  const assignment = data?.entity || {};
  const solved = Boolean(assignment?.solvedByCurrentUser || assignment?.isSolved || assignment?.completedByCurrentUser || assignment?.progressStatus === 'solved');
  const language = assignment?.language || (Array.isArray(assignment?.allowedLanguages) ? assignment.allowedLanguages[0] : '');
  return (
    <div
      className={`course-map-node course-map-node--image${selected ? ' is-selected' : ''}${solved ? ' is-solved' : ''}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="assignment"
      data-taskforge-entity-id={assignment.id || data?.entityId}
      data-taskforge-assignment-type="image-test"
      data-taskforge-agent-state={solved ? 'solved' : 'unsolved'}
      data-taskforge-agent-action="open-assignment"
      aria-label={`Задание с изображением: ${assignment.title || 'Без названия'}`}
    >
      <CourseMapHandles />
      <NodeAccessBadges effects={data?.accessEffects} editorMode={data?.editorMode} />
      <NodeTopline icon={Image} title={assignment.title} />
      <AssignmentFooter assignment={assignment} fallback="Задание" />
      <div className="course-map-image-grid">{Array.from({ length: 9 }, (_, i) => <i key={i} />)}</div>
      <NodeHover entity={assignment} meta={language || null} onAction={data?.onOpen} />
    </div>
  );
}
