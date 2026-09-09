import React from 'react';
import { Database } from 'lucide-react';
import { activateCourseMapAssignmentNode, AssignmentFooter, CourseMapHandles, NodeAccessBadges, NodeHover, NodeTopline } from './CourseMapNodePrimitives';

export default function SqlNode({ data, selected }) {
  const assignment = data?.entity || {};
  const solved = Boolean(assignment?.solvedByCurrentUser || assignment?.isSolved || assignment?.completedByCurrentUser || assignment?.progressStatus === 'solved');
  return (
    <div
      className={`course-map-node course-map-node--sql${selected ? ' is-selected' : ''}${solved ? ' is-solved' : ''}`}
      data-taskforge-automation-id={`assignment-${assignment.id || data?.entityId}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="assignment"
      data-taskforge-entity-id={assignment.id || data?.entityId}
      data-taskforge-assignment-type="sql-test"
      data-taskforge-agent-state={solved ? 'solved' : 'unsolved'}
      data-taskforge-agent-kind="sql-test"
      data-taskforge-agent-action="open-assignment"
      onClick={(event) => activateCourseMapAssignmentNode(event, data, data?.onOpen)}
      onKeyDown={(event) => activateCourseMapAssignmentNode(event, data, data?.onOpen)}
      role={data?.editorMode ? undefined : "link"}
      tabIndex={data?.editorMode ? undefined : 0}
      aria-label={`SQL: ${assignment.title || "SQL"}`}
    >
      <CourseMapHandles />
      <NodeAccessBadges effects={data?.accessEffects} editorMode={data?.editorMode} />
      <NodeTopline icon={Database} title={assignment.title} />
      <AssignmentFooter assignment={assignment} fallback="SQL" />
      <div className="course-map-code-watermark">{'SELECT *'}</div>
      <NodeHover entity={assignment} meta="SQL" onAction={data?.onOpen} />
    </div>
  );
}
