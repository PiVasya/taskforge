import React from 'react';
import { activateCourseMapAssignmentNode, CourseMapHandles, NodeAccessBadges, NodeHover } from './CourseMapNodePrimitives';
import { getCourseMapCodeTerminalModel } from '../courseMapNodeMeta';

export default function CodeTestNode({ data, selected }) {
  const assignment = data?.entity || {};
  const terminal = getCourseMapCodeTerminalModel(assignment);

  return (
    <div
      className={`course-map-node course-map-node--code${selected ? ' is-selected' : ''}${terminal.solved ? ' is-solved' : ''}`}
      data-taskforge-automation-id={`assignment-${assignment.id || data?.entityId}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="assignment"
      data-taskforge-entity-id={assignment.id || data?.entityId}
      data-taskforge-assignment-type="code-test"
      data-taskforge-agent-state={terminal.solved ? 'solved' : 'unsolved'}
      data-taskforge-agent-kind="code-test"
      data-taskforge-agent-action="open-assignment"
      onClick={(event) => activateCourseMapAssignmentNode(event, data, data?.onOpen)}
      onKeyDown={(event) => activateCourseMapAssignmentNode(event, data, data?.onOpen)}
      role={data?.editorMode ? undefined : 'link'}
      tabIndex={data?.editorMode ? undefined : 0}
      aria-label={`Задание с кодом: ${terminal.title}`}
    >
      <CourseMapHandles />
      <NodeAccessBadges effects={data?.accessEffects} editorMode={data?.editorMode} />

      <div className="course-map-code-terminal">
        <div className="course-map-code-terminal-body">
          <div className="course-map-code-terminal-line is-command">
            <span className="course-map-code-terminal-prompt" aria-hidden="true">~#</span>
            <span className="course-map-code-terminal-title">{terminal.title}</span>
            <span className="course-map-code-terminal-cursor" aria-hidden="true" />
          </div>
          {terminal.languageLabel ? (
            <div className="course-map-code-terminal-line is-language">
              <span className="course-map-code-terminal-language">{terminal.languageLabel}</span>
            </div>
          ) : null}
          {terminal.solved ? (
            <div className="course-map-code-terminal-line is-result">
              <span className="course-map-code-terminal-status">{terminal.statusLabel}</span>
            </div>
          ) : null}
        </div>
      </div>

      <NodeHover entity={assignment} meta={terminal.languageLabel || null} />
    </div>
  );
}
