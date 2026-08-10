import React from 'react';
import { Image } from 'lucide-react';
import { AssignmentFooter, CourseMapHandles, NodeHover, NodeTopline } from './CourseMapNodePrimitives';

export default function ImageCodeNode({ data, selected }) {
  const assignment = data?.entity || {};
  return (
    <div
      className={`course-map-node course-map-node--image${selected ? ' is-selected' : ''}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="assignment"
      data-taskforge-entity-id={assignment.id || data?.entityId}
      data-taskforge-assignment-type="image-test"
      data-taskforge-agent-state={assignment?.solvedByCurrentUser || assignment?.isSolved ? 'solved' : 'unsolved'}
      data-taskforge-agent-action="open-assignment"
      aria-label={`Картинки и код: ${assignment.title || 'Без названия'}`}
    >
      <CourseMapHandles />
      <NodeTopline icon={Image} kicker="Картинки · код" />
      <div className="course-map-node-title">{assignment.title || 'Без названия'}</div>
      <AssignmentFooter assignment={assignment} fallback="image-test" />
      <div className="course-map-image-grid">{Array.from({ length: 9 }, (_, i) => <i key={i} />)}</div>
      <NodeHover entity={assignment} meta={`Image test${assignment.language ? ` · ${assignment.language}` : ''}`} onAction={data?.onOpen} />
    </div>
  );
}
