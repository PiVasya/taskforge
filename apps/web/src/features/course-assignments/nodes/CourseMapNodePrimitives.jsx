import React from 'react';
import { Handle, Position } from 'reactflow';
import { ArrowRight, CheckCircle2, Circle } from 'lucide-react';
import { previewAssignmentDescription } from '../courseAssignmentsModel';

export function CourseMapHandles() {
  return (
    <>
      <Handle
        id="in"
        type="target"
        position={Position.Left}
        isConnectableStart={false}
        isConnectableEnd
        className="course-map-handle course-map-handle--in"
      />
      <Handle
        id="out"
        type="source"
        position={Position.Right}
        isConnectableStart
        isConnectableEnd={false}
        className="course-map-handle course-map-handle--out"
      />
    </>
  );
}

export function NodeTopline({ icon: Icon, kicker, badge }) {
  return (
    <div className="course-map-node-topline">
      <span className="course-map-node-icon">{Icon ? <Icon size={17} /> : null}</span>
      <span className="course-map-node-kicker">{kicker}{badge ? <b>{badge}</b> : null}</span>
    </div>
  );
}

export function AssignmentFooter({ assignment, fallback }) {
  const solved = Boolean(assignment?.solvedByCurrentUser || assignment?.isSolved || assignment?.completedByCurrentUser || assignment?.progressStatus === 'solved');
  const language = assignment?.language || (Array.isArray(assignment?.allowedLanguages) ? assignment.allowedLanguages[0] : '') || fallback || '';
  return (
    <div className="course-map-node-footer">
      <span>{language || fallback || 'Задание'}</span>
      <span className={`course-map-node-status${solved ? ' is-solved' : ''}`}>
        {solved ? <CheckCircle2 size={12} /> : <Circle size={11} />}
        {solved ? 'решено' : 'не решено'}
      </span>
    </div>
  );
}

export function NodeHover({ entity, meta, actionLabel = 'Перейти к заданию', onAction, placement = 'bottom', children }) {
  const description = previewAssignmentDescription(entity?.description || '') || 'Описание пока не добавлено.';
  return (
    <div className={`course-map-node-hover course-map-node-hover--${placement}`}>
      <div className="course-map-node-hover-body">
        <div className="course-map-hover-description">{description}</div>
        {children}
        {meta ? <div className="course-map-hover-meta">{meta}</div> : null}
      </div>
      {onAction ? (
        <button type="button" className="course-map-node-hover-action nodrag nopan" onClick={(event) => { event.stopPropagation(); onAction(); }}>
          {actionLabel}<ArrowRight size={14} />
        </button>
      ) : null}
    </div>
  );
}
