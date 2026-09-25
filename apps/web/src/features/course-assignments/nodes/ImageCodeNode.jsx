import React from 'react';
import { Image } from 'lucide-react';
import { activateCourseMapAssignmentNode, AssignmentFooter, CourseMapHandles, NodeAccessBadges, NodeHover, NodeTopline } from './CourseMapNodePrimitives';
import { getAssignmentProgrammingLanguage, getCourseMapAssignmentFooterLabel, getCourseMapLanguageLabel } from '../courseMapNodeMeta';

export default function ImageCodeNode({ data, selected }) {
  const assignment = data?.entity || {};
  const solved = Boolean(assignment?.solvedByCurrentUser || assignment?.isSolved || assignment?.completedByCurrentUser || assignment?.progressStatus === 'solved');
  const language = getAssignmentProgrammingLanguage(assignment);
  const languageLabel = getCourseMapLanguageLabel(language);
  const footerLabel = getCourseMapAssignmentFooterLabel('image-test', assignment, 'Задание');
  return (
    <div
      className={`course-map-node course-map-node--image${selected ? ' is-selected' : ''}${solved ? ' is-solved' : ''}`}
      data-taskforge-automation-id={`assignment-${assignment.id || data?.entityId}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="assignment"
      data-taskforge-entity-id={assignment.id || data?.entityId}
      data-taskforge-assignment-type="image-test"
      data-taskforge-agent-state={solved ? 'solved' : 'unsolved'}
      data-taskforge-agent-kind="image-test"
      data-taskforge-agent-action="open-assignment"
      onClick={(event) => activateCourseMapAssignmentNode(event, data, data?.onOpen)}
      onKeyDown={(event) => activateCourseMapAssignmentNode(event, data, data?.onOpen)}
      role={data?.editorMode ? undefined : "link"}
      tabIndex={data?.editorMode ? undefined : 0}
      aria-label={`Задание с изображением: ${assignment.title || 'Без названия'}`}
    >
      <CourseMapHandles />
      <NodeAccessBadges effects={data?.accessEffects} editorMode={data?.editorMode} />
      <NodeTopline icon={Image} title={assignment.title} />
      <AssignmentFooter assignment={assignment} label={footerLabel} />
      <div className="course-map-image-grid">{Array.from({ length: 9 }, (_, i) => <i key={i} />)}</div>
      <NodeHover entity={assignment} meta={languageLabel || null} />
    </div>
  );
}
